# Installation

## 1. Linux

On Linux, the two parts of the program (the collector itself, as well as the self-hosted web application) 
are two systemd services with:
* configuration inside /etc/mongodb-collector
* logs inside /var/log/mongodb-collector. Logs are auto rotated every day and purged after 10 days.
* binaries inside /opt/mongodb-collector (can be read only)

The services run with a user account named 'collector' created during installation.

Installation is done with RPM.


### 1.1. RHEL 7.x (not CentOS)


Everything must be run as root.

If not done already, you must enable the official .NET repository and install dotnet.
```
subscription-manager repos --enable=rhel-7-server-dotnet-rpms
yum install rh-dotnetcore10
```

Then download the RPM and run it.
```
wget ...
rpm -i mongodb-collector-1.0.0-1.el7.noarch.rpm
```

Go to /etc/mongodb-collector and edit the connection strings inside the two .conf files, the publisher's URL and any other value you wish (see configuration page for the meaning of configuration items).

Then you can enable the collector and the publisher:
```
systemctl enable mongodb-collector-collector
systemctl start mongodb-collector-collector

systemctl enable mongodb-collector-publisher
systemctl start mongodb-collector-publisher
```

### 1.2. CentOS 7.x

Everything must be run as root.

Dotnet is installed manually on CentOS (no official repository).
```
yum install libunwind libicu
curl -sSL -o dotnet.tar.gz https://go.microsoft.com/fwlink/?LinkID=809131
mkdir -p /opt/dotnet && sudo tar zxf dotnet.tar.gz -C /opt/dotnet
ln -s /opt/dotnet/dotnet /usr/local/bin
```

Then download the RPM and run it.
```
wget ...
rpm -i mongodb-collector-1.0.0-1.el7.centos.noarch.rpm
```

Go to /etc/mongodb-collector and edit the connection strings inside the two .conf files, the publisher's URL and any other value you wish (see configuration page for the meaning of configuration items).

Then you can enable the collector and the publisher:
```
systemctl enable mongodb-collector-collector
systemctl start mongodb-collector-collector

systemctl enable mongodb-collector-publisher
systemctl start mongodb-collector-publisher
```

## 2. Windows

TODO.

## 3. Test

Check if the page http://yourserver:5100 (default port) shows you a page with indicators 
(no blank spaces).