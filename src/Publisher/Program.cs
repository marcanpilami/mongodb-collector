using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.PlatformAbstractions;

namespace Publisher
{
    public class Program
    {
        public static String ConfigPath = null;

        public static void Main(string[] args)
        {
            if (args.Length > 0)
            {
                ConfigPath = args[0];
                if (!File.Exists(ConfigPath))
                {
                    return;
                }
            }

            IConfigurationRoot cfg;
            if (ConfigPath == null)
            {
                cfg = new ConfigurationBuilder()
                                            .SetBasePath(PlatformServices.Default.Application.ApplicationBasePath)
                                            .AddJsonFile("appsettings.json", optional: true)
                                            .Build();
            }
            else
            {
                cfg = new ConfigurationBuilder()
                             .AddJsonFile(ConfigPath, optional: false)
                             .Build();
            }

            var host = new WebHostBuilder()
                .UseConfiguration(cfg)
                .UseKestrel()
                .UseContentRoot(Directory.GetCurrentDirectory())
                .UseIISIntegration()
                .UseStartup<Startup>()
                .Build();

            host.Run();
        }
    }
}
