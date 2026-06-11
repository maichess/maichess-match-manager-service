using System.Text;
using Grpc.Net.Client;
using Maichess.Database.V1;
using Maichess.Engine.V1;
using Maichess.MoveValidator.V1;
using Maichess.User.V1;
using MaichessMatchManagerService.Data;
using MaichessMatchManagerService.Events;
using MaichessMatchManagerService.Grpc;
using MaichessMatchManagerService.Kafka;
using MaichessMatchManagerService.Rest;
using MaichessMatchManagerService.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using StackExchange.Redis;

DotNetEnv.Env.Load();
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Database service client
string dbServiceUrl = builder.Configuration["Services:DatabaseService"]
    ?? throw new InvalidOperationException("Services:DatabaseService is not configured");

builder.Services.AddSingleton(
    new Database.DatabaseClient(GrpcChannel.ForAddress(dbServiceUrl)));
builder.Services.AddSingleton<IMatchRepository, MatchRepository>();

// Redis cache for immutable finished-match reads (finished-match docs +
// ListUserMatches pages). Rebuildable from match-db; see the caching-and-read-
// models ADR. Reuses the Redis instance already deployed for Match Maker.
string redisUrl = builder.Configuration.GetConnectionString("Redis")
    ?? throw new InvalidOperationException("ConnectionStrings:Redis is not configured");
builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisUrl));
builder.Services.AddSingleton<IMatchCache, RedisMatchCache>();

// Redis-materialised user replica (user:{id}), fed by the compacted user.events.v1
// topic. Replaces the hot GetUser RPC for username + match-end rating enrichment, with
// a GetUser fallback while the replica warms. Rebuildable from the topic; shared across
// pods. See caching-and-read-models.md (Stage 3).
builder.Services.AddSingleton<IUserReplica, RedisUserReplica>();
builder.Services.AddHostedService<UserReplicaConsumer>();

// Live match read model (match:live:{id}), the CQRS read side for ongoing matches.
// The projector maintains it from match.events.v1; REST live reads overlay its
// volatile fields (fen/clocks/last-move time) onto the durable doc. Rebuildable by
// replaying the log. See caching-and-read-models.md (live match read model).
builder.Services.AddSingleton<ILiveMatchState, RedisLiveMatchState>();
builder.Services.AddHostedService<MatchEventProjectorConsumer>();

// gRPC clients (long-lived singletons — channels and clients are thread-safe)
string userServiceUrl = builder.Configuration["Services:UserService"]
    ?? throw new InvalidOperationException("Services:UserService is not configured");
string moveValidatorUrl = builder.Configuration["Services:MoveValidatorService"]
    ?? throw new InvalidOperationException("Services:MoveValidatorService is not configured");
string engineUrl = builder.Configuration["Services:EngineService"]
    ?? throw new InvalidOperationException("Services:EngineService is not configured");
builder.Services.AddSingleton(
    new Users.UsersClient(GrpcChannel.ForAddress(userServiceUrl)));
builder.Services.AddSingleton(
    new Moves.MovesClient(GrpcChannel.ForAddress(moveValidatorUrl)));
builder.Services.AddSingleton(
    new Bots.BotsClient(GrpcChannel.ForAddress(engineUrl)));

// Real-time fan-out always publishes to socket.outbound.v1; the legacy
// Socket.BroadcastMatchEvent gRPC path was removed in Kafka task 09.
builder.Services.AddSingleton<ISocketBroadcaster, KafkaSocketNotifier>();

// Match creation is event-sourced: consume CreateMatchCommand from match.commands.v1
// and materialize the match with the caller-minted id (replaces inbound gRPC CreateMatch).
builder.Services.AddHostedService<MatchCommandConsumer>();

// Command side (Kafka task 06): the move/resign/draw write path and creation emit facts
// to match.events.v1 through this producer; the validator + projector + engine loop and
// the socket fan-out carry the authoritative result back to clients.
builder.Services.AddSingleton<IMatchEventProducer, KafkaMatchEventProducer>();

// Application services
builder.Services.AddSingleton<MatchService>();
builder.Services.AddHostedService<TimeoutWatchdog>();

// JWT authentication
string jwtKey = builder.Configuration["Jwt:Key"]
    ?? throw new InvalidOperationException("Jwt:Key is not configured");

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero,
        };
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                if (context.Request.Cookies.TryGetValue("access_token", out string? token))
                {
                    context.Token = token;
                }

                return Task.CompletedTask;
            },
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddGrpc();
builder.Services.AddOpenApi();

string otlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")
    ?? "http://otel-collector:4317";

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("match-manager-service"))
    .WithTracing(t => t
        .AddAspNetCoreInstrumentation()
        .AddGrpcClientInstrumentation()
        .AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint)));

WebApplication app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapGrpcService<MatchesGrpcService>();
app.MapMatchesEndpoints();

app.Run();
