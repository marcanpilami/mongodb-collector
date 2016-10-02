Name:		mongodb-agent
Version:	%{_version}
Release:	1%{?dist}
Summary:	Utility to regularly store and publish performance MongoDB data

Group:		Applications/Databases
License:	ASL 2.0
URL:		https://github.com/marcanpilami/mongodb-collector
Source0:	%{name}-%{version}.tar.gz

BuildArch: 	x86_64
BuildRequires:	nodejs,systemd
Requires:	unzip, libunwind, gettext, libcurl, openssl, zlib, libicu, /usr/sbin/useradd, /usr/bin/getent, /usr/bin/true 
AutoReqProv:    no

%description
%{summary}

%prep
# Dotnet cannot be a BuildRequires as on CentOS no RPM package for .NET
%setup -q

%build
# Nothing to do - pre built.

%install
rm -rf %{buildroot}
mkdir -p  %{buildroot}
cp -a * %{buildroot}

%clean
rm -rf %{buildroot}

%files
%defattr(-,collector,collector,-)
/opt/%{name}
/var/log/%{name}
%dir %attr(750, root, collector)  %{_sysconfdir}/%{name}
%attr(640, root, collector) %config(noreplace) %{_sysconfdir}/%{name}/%{name}.conf
%attr(640, root, collector) %config(noreplace) %{_sysconfdir}/%{name}/nlog.config
%{_unitdir}/%{name}.service

%pre
/usr/bin/getent passwd collector >/dev/null || (/usr/sbin/useradd -r -d /opt/%{name} -s /sbin/nologin collector >/dev/null)
/usr/bin/systemctl stop %{name}.service >/dev/null 2>&1 || true

%post
/usr/bin/systemctl daemon-reload

%changelog
* Sat Aug 20 2016 MAG 1.0.0
- First RPM spec
