using System.Text;
using Grpc.Net.Client;
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
using MongoDB.Driver;

DotNetEnv.Env.Load();
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// MongoDB
string mongoConnectionString = builder.Configuration.GetConnectionString("MongoDB")
    ?? throw new InvalidOperationException("ConnectionStrings:MongoDB is not configured");

builder.Services.AddSingleton(_ => new MongoClient(mongoConnectionString));
builder.Services.AddSingleton(sp => sp.GetRequiredService<MongoClient>().GetDatabase("maichess"));
builder.Services.AddSingleton<IMatchRepository, MatchRepository>();

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

// Application services
builder.Services.AddSingleton<MatchEventBroadcaster>();
builder.Services.AddSingleton<MatchService>();

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
