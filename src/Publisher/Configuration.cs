using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Publisher
{
    public class Configuration
    {
        public String MongoDbUrl { get; set; } = "mongodb://localhost:27017?w=majority&wtimeout=5000&journal=true";
        public String MongoDbName { get; set; } = "test";

        public String DetailCollectionName { get; set; } = "mongoperf_detail";
        public String SummaryCollectionName { get; set; } = "mongoperf_summary";
        public int DetailCollectionMaxSizeMb { get; set; } = 100;
    }
}
