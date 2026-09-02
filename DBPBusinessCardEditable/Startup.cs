using DBPBusinessCardEditable.Middleware;
using DBPBusinessCardEditable.Services;
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
            services.AddControllersWithViews();
            services.AddSingleton<CardProfileService>();

            // Session for PIN verification across requests
            services.AddDistributedMemoryCache();
            services.AddSession(o => {
                o.Cookie.HttpOnly  = true;
                o.Cookie.IsEssential = true;
                o.IdleTimeout      = System.TimeSpan.FromMinutes(30);
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
                    Description = "API key for protected endpoints. Default: dbp-api-2026"
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

            app.UseStaticFiles();

            // Rate limiting on /api/* routes
            app.UseMiddleware<RateLimitMiddleware>();

            app.UseRouting();
            app.UseSession();
            app.UseAuthorization();

            app.UseSwagger();
            app.UseSwaggerUI(c =>
            {
                c.SwaggerEndpoint("/swagger/v1/swagger.json", "DBP Business Card API v1");
                c.RoutePrefix    = "swagger";
                c.DocumentTitle  = "DBP API";
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
