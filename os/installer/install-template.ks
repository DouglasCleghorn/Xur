text
lang en_US.UTF-8
keyboard us
timezone UTC --utc
rootpw --lock
selinux --enforcing
firewall --enabled --port=8080:tcp --port=8443:tcp
bootloader --append="bluetooth.disable_ertm=1 console=tty1"
bootc --source-imgref registry:ghcr.io/ublue-os/bazzite-nvidia-open:stable --target-imgref ghcr.io/ublue-os/bazzite-nvidia-open:stable
%onerror
touch /run/xur/install-failed
%end
%post --nochroot --erroronfail
set -eu
target=/mnt/sysroot
test -d "$target/var"
mkdir -p "$target/var/lib/xur" "$target/var/lib/tailscale"
chmod 700 "$target/var/lib/xur" "$target/var/lib/tailscale"
cp /run/xur/install-operation.json "$target/var/lib/xur/install-operation.json"
if test -d /run/xur/form-keys; then cp -a /run/xur/form-keys "$target/var/lib/xur/form-keys"; chmod 700 "$target/var/lib/xur/form-keys"; fi
install -m 600 /run/xur/session-signing.key "$target/var/lib/xur/session-signing.key"
if test -f /run/xur/manager-account.json; then install -m 600 /run/xur/manager-account.json "$target/var/lib/xur/manager-account.json"; fi
if test -f /run/xur/administrator.json; then cp /run/xur/administrator.json "$target/var/lib/xur/administrator.json"; fi
if test -f /var/lib/tailscale/tailscaled.state; then cp /var/lib/tailscale/tailscaled.state "$target/var/lib/tailscale/tailscaled.state"; fi
if test -f /run/xur/timezone; then
  zone=$(cat /run/xur/timezone)
  test -f "/usr/share/zoneinfo/$zone"
  test -f "$target/usr/share/zoneinfo/$zone"
  ln -sfn "/usr/share/zoneinfo/$zone" "$target/etc/localtime"
  install -m 600 /run/xur/timezone "$target/var/lib/xur/timezone"
  if test -f /run/xur/timezone-mode; then install -m 600 /run/xur/timezone-mode "$target/var/lib/xur/timezone-mode"; fi
fi
touch "$target/var/lib/xur/installed"
/usr/bin/bash /usr/libexec/xur-install-manager "$target"
%end
