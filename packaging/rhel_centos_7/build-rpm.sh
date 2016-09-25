#!/bin/bash

cd $(dirname $0)
resDir=$(pwd)
cd $(dirname $0)/../..
repoRoot=$(pwd)

tmpDir=/tmp/build-collector
publishRoot=$tmpDir/publish
rpmRoot=$tmpDir/rpm

# Get version
version=$(grep "^  \"version" ${repoRoot}/src/Collector/project.json | cut -d':' -f2 | sed "s|.*\"\(.*\)-.*|\1|g")

# Main RPM macros
echo "%_topdir ${rpmRoot}
%_tmppath %{_topdir}/tmp
%_version ${version}
%debug_package %{nil}
" >~/.rpmmacros

# Distrib specifics (no dotnet repo package in centos)
grep -i centos /etc/os-release > /dev/null
if [[ $? -eq 0 ]]
then
    # CentOS
    export dotnetPath=/usr/local/bin/dotnet
    
    echo "%_moreRequires %{nil}
%_moreTests true" >>~/.rpmmacros
else
    # RHEL
    export dotnetPath=/opt/rh/rh-dotnetcore10/root/usr/bin/dotnet
    
    echo "%_moreRequires %{nil}
%_moreTests true" >>~/.rpmmacros
fi

# BUILD
#scl enable rh-dotnetcore10 bash
rm -rf $tmpDir
mkdir -p ${publishRoot}/{Publisher,Collector}
dotnet restore src/Publisher
dotnet publish src/Publisher --output ${publishRoot}/Publisher --configuration Release --runtime "centos.7-x64"
dotnet restore src/Collector
dotnet publish src/Collector --output ${publishRoot}/Collector --configuration Release --runtime "centos.7-x64"

# Create TGZ for RPMA
tarRoot=${rpmRoot}/mongodb-collector-${version}
mkdir -p ${rpmRoot}/{RPMS,SRPMS,BUILD,SOURCES,SPECS,tmp}
mkdir -p ${tarRoot}/opt/mongodb-collector
mkdir -p ${tarRoot}/var/log/mongodb-collector
mkdir -p ${tarRoot}/etc/mongodb-collector
mkdir -p ${tarRoot}/usr/lib/systemd/system/

cat ${resDir}/mongodb-collector-collector.service | envsubst > ${tarRoot}/usr/lib/systemd/system/mongodb-collector-collector.service
cat ${resDir}/mongodb-collector-publisher.service | envsubst > ${tarRoot}/usr/lib/systemd/system/mongodb-collector-publisher.service

cp ${publishRoot}/Publisher/appsettings.json ${tarRoot}/etc/mongodb-collector/mongodb-collector-publisher.conf
cp ${publishRoot}/Collector/settings.json    ${tarRoot}/etc/mongodb-collector/mongodb-collector-collector.conf
cat ${publishRoot}/Collector/nlog.config | sed "s|./logs/collector.log|/var/log/mongodb-collector/collector.log|g" > ${tarRoot}/etc/mongodb-collector/nlog.config

cp -r ${publishRoot}/Collector ${tarRoot}/opt/mongodb-collector/
cp -r ${publishRoot}/Publisher ${tarRoot}/opt/mongodb-collector/
rm -f ${tarRoot}/opt/mongodb-collector/Collector/settings.json ${tarRoot}/opt/mongodb-collector/Publisher/appsettings.json  ${tarRoot}/opt/mongodb-collector/Collector/nlog.config

tar -zcf ${rpmRoot}/mongodb-collector-${version}.tar.gz -C $(dirname ${tarRoot}) ./$(basename ${tarRoot})
if [[ $? -ne 0 ]]
then
	exit 1
fi
mv ${rpmRoot}/mongodb-collector-${version}.tar.gz ${rpmRoot}/SOURCES/
rm -rf ${tarRoot}


# Create RPM
cd ${rpmRoot}
cp ${resDir}/mongodb-collector.spec ${rpmRoot}/SPECS/
rpmbuild -ba SPECS/mongodb-collector.spec
cd /tmp

# CleanupA
echo "RPM placed inside your home directory:"
find ${rpmRoot}/RPMS -name "*.rpm" -exec basename {} \; -exec mv {} ~ \;
rm -rf ${tmpDir}
