using System.Text;
using Grpc.Net.Client;
using Maichess.Database.V1;
using Maichess.Engine.V1;
using Maichess.MoveValidator.V1;
using Maichess.User.V1;
using MaichessMatchManagerService.Data;
using MaichessMatchManagerService.Events;
using MaichessMatchManagerService.Grpc;
using MaichessMatchManagerService.Rest;
using MaichessMatchManagerService.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

using SocketSvc = Socket.V1.Socket;

DotNetEnv.Env.Load();
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Database service client
string dbServiceUrl = builder.Configuration["Services:DatabaseService"]
    ?? throw new InvalidOperationException("Services:DatabaseService is not configured");

builder.Services.AddSingleton(
    new Database.DatabaseClient(GrpcChannel.ForAddress(dbServiceUrl)));
builder.Services.AddSingleton<IMatchRepository, MatchRepository>();

// gRPC clients (long-lived singletons — channels and clients are thread-safe)
string userServiceUrl = builder.Configuration["Services:UserService"]
    ?? throw new InvalidOperationException("Services:UserService is not configured");
string moveValidatorUrl = builder.Configuration["Services:MoveValidatorService"]
    ?? throw new InvalidOperationException("Services:MoveValidatorService is not configured");
string engineUrl = builder.Configuration["Services:EngineService"]
    ?? throw new InvalidOperationException("Services:EngineService is not configured");
string socketServiceUrl = builder.Configuration["Services:SocketService"]
    ?? throw new InvalidOperationException("Services:SocketService is not configured");

builder.Services.AddSingleton(
    new Users.UsersClient(GrpcChannel.ForAddress(userServiceUrl)));
builder.Services.AddSingleton(
    new Moves.MovesClient(GrpcChannel.ForAddress(moveValidatorUrl)));
builder.Services.AddSingleton(
    new Bots.BotsClient(GrpcChannel.ForAddress(engineUrl)));
builder.Services.AddSingleton(
    new SocketSvc.SocketClient(GrpcChannel.ForAddress(socketServiceUrl)));

// Application services
builder.Services.AddSingleton<SocketNotifier>();
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
