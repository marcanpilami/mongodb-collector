using Collector;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.PlatformAbstractions;
using MongoDB.Bson;
using NLog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace monitoringexe
{
    public static class Program
    {
        private static Logger Logger = LogManager.GetCurrentClassLogger();
        internal static readonly Configuration Configuration = new Configuration();
        private static List<ClusterHandler> clusters;
        private static String ConfigPath = null;
        private static ZabbixAgent agent;

        private static Timer configTimer;

        static void Main(string[] args)
        {
            if (args.Length > 0)
            {
                ConfigPath = args[0];
                if (!File.Exists(ConfigPath))
                {
                    return;
                }

                if (File.Exists(Path.Combine(Path.GetDirectoryName(ConfigPath), "nlog.config")))
                {
                    NLog.LogManager.Configuration = new NLog.Config.XmlLoggingConfiguration(Path.Combine(Path.GetDirectoryName(ConfigPath), "nlog.config"), true);
                }
            }
            Start();

            Thread.Sleep(Timeout.Infinite);
        }

        public static void Start()
        {
            LoadConfiguration();
            clusters = new List<ClusterHandler>();
            foreach (Connection cnx in Configuration.Connections)
            {
                clusters.Add(new ClusterHandler(Configuration, cnx));
            }

            if (Configuration.EnableZabbixAgent)
            {
                agent = new ZabbixAgent(Configuration);
            }

            //configTimer = new Timer(T_Elapsed, null, 0, Configuration.RefreshConfigurationMinute * 60 * 1000);
        }

        public static void Stop()
        {
            foreach (ClusterHandler p in clusters)
            {
                p.Dispose();
            }
        }

        public static void LoadConfiguration()
        {
            var builder = new ConfigurationBuilder().SetBasePath(PlatformServices.Default.Application.ApplicationBasePath);
            if (ConfigPath != null)
            {
                builder = builder.AddJsonFile(ConfigPath);
            }
            else
            {
                builder = builder.AddJsonFile("settings.json");
            }
            var config = builder.Build();
            Configuration.Items.Clear();
            Configuration.Collections.Clear();
            Configuration.Connections.Clear();
            config.Bind(Configuration);
        }

        private static void T_Elapsed(object state)
        {
            LoadConfiguration();
        }

        public static long ToLong(this BsonValue value)
        {
            return value.IsInt64 ? value.AsInt64 : value.IsInt32 ? (long)value.AsInt32 : (long)value.AsDouble;
        }
    }
}
