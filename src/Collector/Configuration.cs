using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace monitoringexe
{
    class Configuration
    {
        public Boolean EnableDataCollection { get; set; } = true;
        public int RefreshPeriodSecond { get; set; } = 60;
        public int RefreshConfigurationMinute { get; set; } = 1;
        public Connection ResultsStorageConnection { get; set; } = null;
        public List<String> MonitoredConnectionStrings { get; private set; } = new List<String>();
        public List<Item> Items { get; set; } = new List<Item>();
        public List<CollectionContainer> Collections { get; set; } = new List<CollectionContainer>();
        public String DetailCollectionName { get; set; } = "mongoperf_detail";
        public String SummaryCollectionName { get; set; } = "mongoperf_summary";
        public int DetailCollectionMaxSizeMb { get; set; } = 100;
        public bool EnableZabbixAgent { get; set; } = true;
        public String ZabbixAgentListeningInterface { get; set; } = "localhost";
        public int ZabbixAgentListeningPort { get; set; } = 10051;
    }

    class Connection
    {
        public String ConnectionString { get; set; }
        public String DatabaseName { get; set; }

        public String NodeName
        {
            get
            {
                if (ConnectionString.Contains("?"))
                {
                    return ConnectionString.Split('/')[2].Split('?')[0].ToLowerInvariant();
                }
                else
                {
                    return ConnectionString.Split('/')[2].ToLowerInvariant(); ;
                }
            }
        }
    }

    class Item
    {
        public String Path { get; set; }
        public String JsonUniqueName { get; set; }
        public ItemMode Mode { get; set; } = ItemMode.diff;
        public bool InSummary { get; set; } = false;
        public float Multiplier { get; set; } = 1;

        public String Root { get { return Path.Split('/')[0]; } }

        public IEnumerable<String> PathSegments
        {
            get
            {
                return Path.Replace("//", "|||||").Split('/').Select(t => t.Replace("|||||", "/")).Skip(1);
            }
        }
    }

    class CollectionContainer
    {
        public List<String> Include { get; set; } = new List<string>();
        public List<String> Exclude { get; set; } = new List<string>();
    }

    enum ItemMode
    {
        diff, value
    }
}
