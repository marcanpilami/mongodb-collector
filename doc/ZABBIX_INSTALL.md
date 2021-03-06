# Use of the Zabbix MongoDB Agent

The collector service is a full fedged Zabbix agent (including discovery) that can be queried by a Zabbix server.

You must first perform the standard [installation](./INSTALL.md) before being able to use the agent.

The agent is enabled by default so the agent should be up at the end of the install procedure without 
further complication.
You may however want to change the following parameters inside the configuration file 
(on Linux, inside /etc/mongodb-collector):

```
  "EnableZabbixAgent": true,
  "ZabbixAgentListeningInterface": "0.0.0.0",
  "ZabbixAgentListeningPort": 10049,
  "ZabbixAgentQueryCacheSecond": 30,
```

The first parameter can disable the Zabbix agent (this has no impact on the periodic data 
collection - it justs switches the Zabbix agent part of the product off).

The second defines the interface on which to listen to. The third the port to use. The default is 
10049, which is the default Zabbix agent port (10050) minus one, so as to allow both to run alongside
with default configuration.

ZabbixAgentQueryCacheSecond controls the data cache. As Zabbix does one query per item, it may result in
a huge quantity of queries inside the monitored MongoDB instance. This is not adapted to Mongo, as all
items actually come from a grand total of four different documents - so we actually query the same
documents over and over, a huge waste of queries. This parameter allows to cache the result of these queries
for a given time (in seconds).

Inside Zabbix, import the two templates which are [here](../packaging/zabbix/zbx_export_templates.xml).

Define a new host for the collector (and NOT for the different MongoDB instances). Apply the template named 
"MongoDB monitor". Thanks to Zabbix' discovery mechanisms, one host per MongoDB instance will be created 
in the next minute.
