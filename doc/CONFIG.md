# Collector configuration

Main structure:
```
{
    "RefreshPeriodSecond": 60,
    "RefreshConfigurationMinute": 10,
    "RefreshClusterTopologyMinute": 10,

    "MonitoredConnectionStrings": [
        "ConnectionString": "mongodb://localhost:27017?wtimeout=5000&journal=false&replicaSet=rsname",
    ],

    ResultsStorageConnection: {
        "ConnectionString": "mongodb://localhost:27017?wtimeout=5000&journal=false&replicaSet=rsname",
        "DatabaseName": "datacollector",
    },

    "Items": [
        {
            "Path": "serverStatus/metrics/commands/aggregate/total",
            "JsonUniqueName": "queries_aggregate_count",
            "Mode": "diff",
            "InSummary": true
        },
        {
            "Path": "serverStatus/metrics/commands/count/total",
            "JsonUniqueName": "queries_count_count",
            "InSummary": true
        }
    ]
}
```

### General parameters

RefreshPeriodSecond: for later use. Should be left at 60 seconds for now.

RefreshConfigurationMinute: the service will re-read its configuration file (items section only) every RefreshConfigurationMinute minutes. So there is no need to restart the service after an item configuration change.

EnableDataCollection: optional, default si true. If true, the agent will collect data every RefreshPeriodSecond and store it inside a database. If false, the agent's only interest is as a Zabbix agent (see the specific Zabbix doc).

RefreshClusterTopologyMinute: optional, default is 10. Period in minutes for the discovery of new (or removed) nodes inside a replica set.

### MonitoredConnectionStrings

This section contains all the MongoDB clusters or single instances that should be monitored. If monitoring a replica set, a single connection is required, and 
the program will automatically connect to all nodes. Do not forget the "replicaSet" parameter in your connection string in that case.

For SECONDARY nodes, the connection will be read only.

For PRIMARY nodes, three collections will be added containing the monitoring data for the whole replica set.

The different parameters that can be set:
* ConnectionString - compulsory - a standard MongoDB connection string. It will usually be of on these forms:
  * Single instance without authentication: "mongodb://mg1.domain.com:27017"
  * Replica set without authentication: "mongodb://mg1.domain.com:27017,mg2.domain.com:27017?replicaSet=myreplicaset"
  * Single instance with authentication (user defined in the admin DB): "mongodb://username:password@mg1.domain.com:27017/admin
  * Replica set with authentication: "mongodb://username:password@mg1.domain.com:27017/admin?replicaSet=myreplicaset"

### ResultsStorageConnection

This specifies where the collected data should be stored. It can be the inside one of the monitored instances.

### Items

This section contains all the items that should be monitored. Please note that the package comes with a default item configuration that should be a good start for most environments, and therefore few modifications are expected in this section.

Each item may have different parameters:
* Path - compulsory - the path inside the different MongoDB documents. See below.
* JsonUniqueName - compulsory - must be unique and a valid javascript variable name - the name this item will be stored as. It's "key", which is used inside queries.
* Mode - optional, default is diff
  * diff: the value stored is the delta between the current measure and the previous measure (one measure interval ago).
  * value: the value is stored as-is
* InSummary - optional, default is false - set to true for this item to be added to the summary document
* Multiplier - optional, default is 1 - the value will be multiplied by this value beofre being stored. This helps convert value to sensible units.

#### Item path

This service can fetch values from different documents inside a MongoDB instance(see MongoDb doc on these commands for details):
* top: a few activity indicators
* serverStatus: global instance statistics (by far the most interesting document).
* dbStats: per-database statistics - the collector will sum the results of all databases.
* rsStatus: the status of the current node inside the replica set.

These documents are complex and we only need to extract a few values from them. These items are deignated by
* a root, that is one of the documents listed above
* a path corresponding to the navigation insde the JSON document

For example, the count of the "count commands" will be serverStatus/metrics/commands/count/total.


#### Hard-coded items

Items that are hard coded, only present in the "latest status" document and not historized:
* MongoDB version
* uptime
* host name
* host FQDN (may be null as it depends on reverse DNS resolution by the database)
* the queries stored inside "system.profile" in all databases (the traced queries) are always historized when data collection is enabled