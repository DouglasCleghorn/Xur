#!/usr/bin/env bash
# Read-only hardware probes; only the report file is written.
# Run on the physical machine. No IP configuration, file contents or tokens.
set -u
umask 077
report="xur-hardware-$(date -u +%Y%m%dT%H%M%SZ).txt"
section() { printf '\n===== %s =====\n' "$1"; }
have() { command -v "$1" >/dev/null 2>&1; }
privileged() {
  if test "$(id -u)" = 0; then "$@"; else sudo -n "$@"; fi
}
{
  section "OS AND KERNEL"
  cat /etc/os-release 2>/dev/null || true
  uname -a
  printf 'Architecture: '; uname -m
  printf 'Firmware: '; test -d /sys/firmware/efi && echo UEFI || echo BIOS
  have mokutil && mokutil --sb-state || echo "mokutil unavailable or failed"
  printf 'Virtualization: '
  if have systemd-detect-virt; then systemd-detect-virt || true; else echo unavailable; fi

  section "SYSTEM"
  for field in system-manufacturer system-product-name baseboard-manufacturer \
    baseboard-product-name bios-vendor bios-version bios-release-date; do
    printf '%s: ' "$field"
    privileged dmidecode -s "$field" 2>/dev/null || echo "unavailable (probe or privilege)"
  done
  section "UNPRIVILEGED DMI FALLBACK"
  for field in sys_vendor product_name board_vendor board_name bios_vendor bios_version bios_date; do
    printf '%s: ' "$field"
    cat "/sys/class/dmi/id/$field" 2>/dev/null || echo unavailable
  done

  section "CPU AND NUMA"
  lscpu
  have numactl && numactl --hardware || echo "numactl unavailable or failed"
  free -h
  section "PCI TOPOLOGY AND DRIVERS"
  lspci -Dnnk
  section "PCI TREE"
  lspci -tv
  section "GPU PCI LINK DETAILS"
  # -n emits numeric class IDs; the original -nn output has textual class names.
  lspci -Dn | awk '$2 ~ /^03(00|02|80):/ {print $1}' |
    while read -r device; do
      printf '\n--- %s ---\n' "$device"
      privileged lspci -s "$device" -vv 2>/dev/null || {
        echo "Privileged PCI details unavailable; using unprivileged probe"
        lspci -s "$device" -vv
      }
    done
  section "IOMMU GROUPS"
  if test -d /sys/kernel/iommu_groups; then
    find /sys/kernel/iommu_groups -type l -printf '%h %f -> %l\n' | sort -V
  else echo "No IOMMU groups exposed"; fi
  section "DRM DEVICES"
  ls -l /dev/dri 2>/dev/null || true
  for device in /sys/class/drm/card*/device; do
    test -e "$device" || continue
    printf '\n--- %s ---\n' "$device"
    sed -n '1,80p' "$device/uevent" 2>/dev/null || true
  done
  section "DISPLAY CONNECTORS"
  for connector in /sys/class/drm/card*-*/status; do
    test -e "$connector" || continue
    printf '%s: ' "${connector%/status}"
    cat "$connector"
    modes="${connector%/status}/modes"
    test -s "$modes" && sed 's/^/  mode: /' "$modes"
  done
  section "NVIDIA"
  if have nvidia-smi; then
    nvidia-smi --query-gpu=index,name,pci.bus_id,memory.total,driver_version,display_active,display_mode --format=csv,noheader
    printf '\n--- topology ---\n'; nvidia-smi topo -m || true
    printf '\n--- NVLink state ---\n'; nvidia-smi nvlink --status || true
  else echo "nvidia-smi unavailable"; fi
  section "AMD"
  have amd-smi && amd-smi static --gpu all || echo "amd-smi unavailable or failed"
  have rocm-smi && rocm-smi --showproductname --showdriverversion --showmeminfo vram || echo "rocm-smi unavailable or failed"
  section "INTEL"
  have xpu-smi && xpu-smi discovery -l || echo "xpu-smi unavailable or failed"
  have intel_gpu_top && intel_gpu_top -L || echo "intel_gpu_top unavailable or failed"
  section "VULKAN"
  have vulkaninfo && vulkaninfo --summary || echo "vulkaninfo unavailable or failed"
  section "WHOLE STORAGE DEVICES"
  lsblk -d -e 7 -o NAME,PATH,TYPE,SIZE,MODEL,VENDOR,TRAN,ROTA,RO,WWN,SERIAL
  section "USB"
  have lsusb && lsusb || echo "lsusb unavailable or failed"
  have lsusb && lsusb -tv || true
  section "AUDIO"
  have aplay && aplay -l || echo "aplay unavailable or failed"
  section "NETWORK CONTROLLERS"
  lspci -Dnnk | awk '
    /^[0-9a-f]+:.*(Ethernet|Network controller)/ {show=1}
    /^[0-9a-f]+:/ && $0 !~ /(Ethernet|Network controller)/ {show=0}
    show {print}
  '
  section "LOADED GPU MODULES"
  lsmod | awk 'NR == 1 || $1 ~ /^(nvidia|nouveau|amdgpu|radeon|i915|xe)$/'
} > "$report" 2>&1
printf 'Created %s/%s\n' "$PWD" "$report"
printf 'Private report: includes PCI/USB topology and disk serials. Review before sharing.\n'
