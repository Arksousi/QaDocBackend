using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using QaDocBackend.Data;
using QaDocBackend.Infrastructure;
using QaDocBackend.Models;
using QaDocBackend.Repositories;
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers(options => options.Filters.Add<GuestReadOnlyFilter>());
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    // "Authorize" button in Swagger UI: paste the token from POST /api/auth/login.
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT", In = ParameterLocation.Header
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }] = []
    });
});
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddSingleton<ISqlConnectionFactory, SqlConnectionFactory>();
builder.Services.AddScoped<IProjectRepository, ProjectRepository>();
builder.Services.AddScoped<IProjectMemberRepository, ProjectMemberRepository>();
builder.Services.AddScoped<IFolderRepository, FolderRepository>();
builder.Services.AddScoped<IAttachmentRepository, AttachmentRepository>();
builder.Services.AddScoped<ITicketRepository, TicketRepository>();
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<IProjectAccessService, ProjectAccessService>();
// ---------- Authentication (JWT) ----------
var jwt = builder.Configuration.GetSection("Jwt").Get<JwtSettings>() ?? new JwtSettings();
var signingKey = jwt.SigningKey(); // fails fast at startup when the key is missing
builder.Services.AddSingleton(jwt);
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<IPasswordHasher<UserRecord>, PasswordHasher<UserRecord>>();
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = signingKey,
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.FromMinutes(1),
            NameClaimType = JwtRegisteredClaimNames.UniqueName,
            RoleClaimType = ClaimTypes.Role
        };
        options.Events = new JwtBearerEvents
        {
            // A <video src> cannot send an Authorization header, so attachment downloads may carry
            // the token as ?access_token=. Deliberately confined to that one path: the token would
            // otherwise end up in browser history and server logs for every request.
            OnMessageReceived = context =>
            {
                if (context.Request.Path.StartsWithSegments("/api/attachments")
                    && context.Request.Query.TryGetValue("access_token", out var fromQuery))
                {
                    context.Token = fromQuery;
                }
                return Task.CompletedTask;
            },
            // Re-check the account on every request: deactivation, password resets and
            // role changes take effect immediately instead of when the token expires.
            OnTokenValidated = async context =>
            {
                var principal = context.Principal!;
                // A guest token names no account, so there is nothing to re-check. It carries no
                // role either: GuestReadOnlyFilter and IProjectAccessService decide what it reaches.
                if (principal.IsGuest()) return;

                var users = context.HttpContext.RequestServices.GetRequiredService<IUserRepository>();
                bool validId = int.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub), out int userId);
                var user = validId ? await users.GetByIdAsync(userId) : null;
                if (user == null || !user.IsActive || principal.FindFirstValue(QaDocClaims.TokenVersion) != user.TokenVersion.ToString())
                {
                    context.Fail("Session is no longer valid.");
                    return;
                }
                ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(ClaimTypes.Role, user.Role));
            }
        };
    });
// Every endpoint requires a signed-in user unless marked [AllowAnonymous].
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
// ---------- CORS ----------
// Allowed front-end origins come from config: "Cors:Origins" (e.g. Cors__Origins__0 in Railway variables).
// Defaults to the Angular dev server if nothing is configured.
// An entry may contain * for one label part, e.g. https://qadocfrontend-*-qad-oc.vercel.app to match
// every Vercel preview deployment, in addition to your stable production domain.
var corsOrigins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>() is { Length: > 0 } origins
    ? origins
    : ["http://localhost:4200"];
var corsPatterns = corsOrigins
    .Select(o => new System.Text.RegularExpressions.Regex(
        "^" + System.Text.RegularExpressions.Regex.Escape(o.Trim().TrimEnd('/')).Replace(@"\*", "[a-z0-9-]*") + "$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase))
    .ToArray();
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAngularApp", policy =>
    {
        policy.SetIsOriginAllowed(origin => corsPatterns.Any(p => p.IsMatch(origin)))
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});
var app = builder.Build();
// Create or update the database tables before serving requests.
await SchemaInitializer.ApplyAsync(app.Services);
app.UseExceptionHandler();
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "QaDoc API v1");
    c.RoutePrefix = "swagger";
});
app.UseRouting();
app.UseCors("AllowAngularApp"); // MUST come after UseRouting, before UseAuthorization/MapControllers
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.Run();