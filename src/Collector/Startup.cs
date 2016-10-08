using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using monitoringexe;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Localization;
using System.Globalization;

namespace agent.web
{
    public class Startup
    {
        readonly IConfiguration Configuration;

        public Startup(IHostingEnvironment env)
        {
            Configuration = Program.Section;
        }

        // This method gets called by the runtime. Use this method to add services to the container.
        // For more information on how to configure your application, visit http://go.microsoft.com/fwlink/?LinkID=398940
        public void ConfigureServices(IServiceCollection services)
        {
            services.Configure<Configuration>(Configuration);
            services.AddLocalization(options => options.ResourcesPath = "Resources");
            services.AddMvcCore().
                AddViewLocalization(LanguageViewLocationExpanderFormat.Suffix).
                AddJsonFormatters(j => { j.Converters.Add(new BsonDocumentConverter()); }).
                AddXmlSerializerFormatters().
                AddRazorViewEngine();
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IHostingEnvironment env, ILoggerFactory loggerFactory)
        {
            loggerFactory.AddConsole();

            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseStaticFiles();

            app.UseRequestLocalization(new RequestLocalizationOptions
            {
                DefaultRequestCulture = new RequestCulture("en"),
                // Formatting numbers, dates, etc.
                SupportedCultures = { new CultureInfo("en"), new CultureInfo("fr") },
                // UI strings that we have localized.
                SupportedUICultures = { new CultureInfo("en"), new CultureInfo("fr") },
            });

            app.UseMvc();
        }
    }
}
