# MongoDB metrics collector agent

This is a small utility with no prentions whatsoever trying to solve very simply the common question 
*how do I extract the many monitoring items of MongoDB toward my monitoring or capacity planning system*?

When all modules are enabled, it provides:
* Data collection and historization inside a MongoDB database of a configurable set of data points.
* An optional web application (self hosted, can be put behind a reverse proxy) for first level analysis providing:
    * a web dashboard giving the instant health of you DB as well as the ability to query the history
    * a set of very simple web services (JSON or XML) to enable you to retrieve the data from another 
      system (Nagios, RRDB, ...)
* A Zabbix agent, and therefore can be queried directly from Zabbix (including auto discovery) - templates are provided.

It is extremely easy to use:    
* Runs on Windows and Linux.
* Packaged as RPM (Linux) and soon as Chocolatey (Windows). It even includes the runtime it uses (dotnet core) to ease installation on all platforms.
* Compatible both with single instances and replica sets. New replicas (and removed replica) are discovered without restart or configuration change.
* With or without database authentication.
* Can be deployed either on the DB server itself, or on another machine to create a centralized monitoring system for multiple instances (as it uses standard MongoDB connections).
* Only collect the metrics you need - it is fully customizable, and provided with a default configuratin which should suit most users.
* All three modules are optional - once again, just select what you need.

# Installation

See [the installation documentation](./doc/INSTALL.md). Note that a single installation 
can monitor multiple MongoDB instances, so a single centralized instance may be enough for all your servers.

After the install, if you want to use the Zabbix MongoDB agent, follow [this procedure](./doc/ZABBIX_INSTALL.md).

# Configuration

See [this document](./doc/CONFIG.md).
