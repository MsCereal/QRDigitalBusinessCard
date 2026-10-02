using DBPBusinessCardEditable.Data;
using DBPBusinessCardEditable.Middleware;
using DBPBusinessCardEditable.Models;
using DBPBusinessCardEditable.Services;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.OpenApi.Models;

namespace DBPBusinessCardEditable
{
    public class Startup
    {
        public Startup(IConfiguration configuration) => Configuration = configuration;
        public IConfiguration Configuration { get; }

        public void ConfigureServices(IServiceCollection services)
        {
            // Antiforgery — on validation failure redirect to login instead of raw 400
            services.AddAntiforgery(o =>
            {
                o.Cookie.Name         = "__dbpaf";
                o.Cookie.HttpOnly     = true;
                o.Cookie.SameSite     = Microsoft.AspNetCore.Http.SameSiteMode.Lax;
                o.Cookie.SecurePolicy = Microsoft.AspNetCore.Http.CookieSecurePolicy.None;
            });

            // Register MVC with antiforgery exception filter so expired tokens redirect
            // to login instead of showing a raw HTTP 400 page
            services.AddControllersWithViews(options =>
            {
                options.Filters.Add<DBPBusinessCardEditable.Filters.AntiforgeryExceptionFilter>();
            });

            // ── Windows Negotiate Auth (Kerberos / NTLM) ────────────────────
            services.AddAuthentication(NegotiateDefaults.AuthenticationScheme)
                    .AddNegotiate();
            services.AddAuthorization();

            // ── LDAP / Active Directory ──────────────────────────────────────
            services.Configure<DirectoryOptions>(Configuration.GetSection("Directory"));
            services.AddMemoryCache();
            services.AddSingleton<LdapDirectoryService>();

            // ── SQLite data layer ────────────────────────────────────────────
            services.AddSingleton<DatabaseService>();
            services.AddSingleton<SqliteCardService>();

            // ── Publish service ──────────────────────────────────────────────
            services.AddSingleton<PublishService>();

            // ── Email notifications ──────────────────────────────────────────
            services.AddSingleton<EmailService>();

            // ── Session (login session cookie) ───────────────────────────
            var sessionMinutes = Configuration.GetValue<int>("Security:SessionTimeoutMinutes", 480);
            services.AddDistributedMemoryCache();
            services.AddSession(o =>
            {
                o.Cookie.Name         = "__dbpbiz";
                o.Cookie.HttpOnly     = true;
                o.Cookie.IsEssential  = true;
                // Lax allows the cookie to be sent on same-site navigations including
                // plain HTTP — safe for the internal-only app, tighten to Strict in prod over HTTPS
                o.Cookie.SameSite     = Microsoft.AspNetCore.Http.SameSiteMode.Lax;
                o.Cookie.SecurePolicy = Microsoft.AspNetCore.Http.CookieSecurePolicy.None;
                o.IdleTimeout         = System.TimeSpan.FromMinutes(sessionMinutes);
            });

            services.AddSwaggerGen(c =>
            {
                c.SwaggerDoc("v1", new OpenApiInfo
                {
                    Title       = "DBP Digital Business Card API",
                    Version     = "v1",
                    Description = "REST API for managing DBP employee digital business cards."
                });
                c.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
                {
                    Name        = "X-Api-Key",
                    In          = ParameterLocation.Header,
                    Type        = SecuritySchemeType.ApiKey,
                    Description = "API key for protected endpoints."
                });
                c.AddSecurityRequirement(new OpenApiSecurityRequirement
                {
                    {
                        new OpenApiSecurityScheme
                        {
                            Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "ApiKey" }
                        },
                        new string[] {}
                    }
                });
            });
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
                app.UseDeveloperExceptionPage();
            else
                app.UseExceptionHandler("/Home/Error");

            // Catch antiforgery token failures (expired session) and redirect to login
            // instead of showing a raw HTTP 400 page
            app.Use(async (context, next) =>
            {
                try
                {
                    await next(context);
                }
                catch (Microsoft.AspNetCore.Antiforgery.AntiforgeryValidationException)
                {
                    context.Response.Redirect("/login?reason=session_expired");
                }
            });

            // Security headers (OWASP / CSP) — before any other middleware
            app.UseMiddleware<SecurityHeadersMiddleware>();

            app.UseStaticFiles();

            // Serve the generated static card files from the publish output folder
            var publishPath = app.ApplicationServices
                .GetRequiredService<IConfiguration>()["Publishing:OutputPath"];
            if (string.IsNullOrWhiteSpace(publishPath))
                publishPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "DBPBizCard", "published");

            Directory.CreateDirectory(publishPath);
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(publishPath),
                RequestPath  = "/published"
            });

            // Rate limiting on /api/* routes
            app.UseMiddleware<RateLimitMiddleware>();

            // Session before routing so antiforgery and session data are available
            app.UseSession();

            app.UseRouting();

            // Auth must come before Authorization
            app.UseAuthentication();
            app.UseAuthorization();

            app.UseSwagger();
            app.UseSwaggerUI(c =>
            {
                c.SwaggerEndpoint("/swagger/v1/swagger.json", "DBP Business Card API v1");
                c.RoutePrefix   = "swagger";
                c.DocumentTitle = "DBP API";
            });

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
                endpoints.MapControllerRoute(
                    name: "default",
                    pattern: "{controller=Home}/{action=Index}/{id?}");
            });
        }
    }
}
