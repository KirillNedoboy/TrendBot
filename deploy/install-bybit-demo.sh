#!/usr/bin/env bash
set -Eeuo pipefail

if [[ $# != 4 || $(id -u) != 0 ]]; then
    printf 'Usage (root): install-bybit-demo.sh ARCHIVE SHA256 RELEASE_ID ENCRYPTED_CREDENTIAL_DIRECTORY\n' >&2
    exit 2
fi
archive=$1
expected_sha=$2
release_id=$3
credential_source=$4
[[ $archive == /* && -f $archive && ! -L $archive ]]
[[ $expected_sha =~ ^[a-f0-9]{64}$ ]]
[[ $release_id =~ ^[a-z0-9][a-z0-9-]{0,63}$ ]]
[[ $credential_source =~ ^/etc/credstore.encrypted/tradingbot-bybit-demo-[a-f0-9]{32}$ ]]
[[ -d $credential_source && ! -L $credential_source && -f $credential_source/ready ]]
[[ $(stat -c '%u:%a' "$credential_source") == 0:700 ]]
for name in bybit-demo-api-key bybit-demo-api-secret; do
    [[ -f $credential_source/$name.cred && ! -L $credential_source/$name.cred ]]
    [[ $(stat -c '%u:%a' "$credential_source/$name.cred") == 0:600 ]]
done
printf '%s  %s\n' "$expected_sha" "$archive" | sha256sum --check --status

unit=tradingbot-bybit-demo.service
app_root=/opt/tradingbot-bybit-demo
config_root=/etc/tradingbot-bybit-demo
credential_root=/etc/credstore.encrypted/tradingbot-bybit-demo
state_root=/var/lib/tradingbot-bybit-demo
private_state_root=/var/lib/private/tradingbot-bybit-demo
unit_file=/etc/systemd/system/$unit
if [[ $(systemctl show "$unit" -p LoadState --value) != not-found \
    || $(systemctl show "$unit" -p ActiveState --value) != inactive \
    || $(systemctl show "$unit" -p MainPID --value) != 0 \
    || -n $(systemctl list-unit-files "$unit" --no-legend --no-pager) ]]; then
    printf 'Refusing occupied systemd unit identity: %s\n' "$unit" >&2
    exit 2
fi
for target in "$app_root" "$config_root" "$credential_root" "$state_root" "$private_state_root" "$unit_file" \
    "/etc/systemd/system/$unit.d" "/run/systemd/system/$unit" "/run/systemd/system/$unit.d" \
    "/usr/lib/systemd/system/$unit" "/usr/lib/systemd/system/$unit.d" "/run/systemd/transient/$unit"; do
    if [[ -e $target || -L $target ]]; then
        printf 'Refusing existing deployment target: %s\n' "$target" >&2
        exit 2
    fi
done

stage=extracting
activated=false
cleanup() {
    local status=$?
    if (( status != 0 )); then
        if $activated; then
            if ! systemctl disable --now "$unit"; then
                printf 'Could not disable the new Demo service; inspect it immediately.\n' >&2
            fi
        fi
        printf 'Demo installation failed at %s; files retained for inspection. No entry approval was created.\n' "$stage" >&2
    fi
}
trap cleanup EXIT

mkdir -m 0755 -- "$app_root"
mkdir -m 0755 -- "$app_root/releases"
release=$app_root/releases/$release_id
mkdir -m 0755 -- "$release"
python3 -I - "$archive" "$release" <<'PY'
import pathlib, sys, tarfile
with tarfile.open(sys.argv[1], 'r:gz') as bundle:
    members = bundle.getmembers()
    if len(members) > 4096 or sum(m.size for m in members) > 1024 * 1024 * 1024:
        raise ValueError('bundle-size')
    for member in members:
        path = pathlib.PurePosixPath(member.name)
        if path.is_absolute() or '..' in path.parts or not (member.isfile() or member.isdir()):
            raise ValueError('bundle-path-or-type')
    bundle.extractall(sys.argv[2], members=members)
PY
chown -R root:root "$release"
chmod -R go-w "$release"
chmod 0755 "$release/TradingBot.App"
for name in TradingBot.App.dll bybit-demo.example.json signal-only.flow-25.example.json signal-only.donchian-flow-30m.example.json signal-only.donchian-flow.example.json "$unit"; do
    [[ -f $release/$name && ! -L $release/$name ]]
done

stage=public-check
public_check_unit=tradingbot-demo-check-$(systemd-id128 new).service
systemd-run --unit="$public_check_unit" --quiet --wait --pipe --collect \
    -p DynamicUser=yes -p WorkingDirectory=/ -p PrivateTmp=yes \
    -p ProtectSystem=strict -p ProtectHome=yes -p NoNewPrivileges=yes \
    -p CapabilityBoundingSet= -p LimitCORE=0 -p UMask=0077 \
    -p MemoryMax=2560M -p CPUQuota=200% -p RuntimeMaxSec=600 \
    -p TimeoutStopSec=45 -p Environment=DOTNET_EnableDiagnostics=0 \
    "$release/TradingBot.App" bybit-demo check --config "$release/bybit-demo.example.json" \
    --flow "$release/signal-only.flow-25.example.json" \
    --donchian-30m "$release/signal-only.donchian-flow-30m.example.json" \
    --donchian-1h "$release/signal-only.donchian-flow.example.json"

stage=credential-mounts
mkdir -m 0755 -- "$config_root"
mkdir -m 0700 -- "$credential_root"
for name in bybit-demo-api-key bybit-demo-api-secret; do
    install -o root -g root -m 0600 -- "$credential_source/$name.cred" "$credential_root/$name.cred"
done
ln -s -- "releases/$release_id" "$app_root/current"
[[ ! -e $config_root/service-approval.json && ! -L $config_root/service-approval.json ]]

stage=unit-validation
install -o root -g root -m 0644 -- "$release/$unit" "$unit_file"
systemd-analyze verify --man=no "$unit_file"
systemctl daemon-reload
[[ $(systemctl show "$unit" -p FragmentPath --value) == "$unit_file" ]]
[[ -z $(systemctl show "$unit" -p DropInPaths --value) ]]
stage=starting-monitor-only
activated=true
systemctl enable --now "$unit"
systemctl is-active --quiet "$unit"
printf 'DEMO_MONITOR_INSTALLED: release=%s; entry approval absent; readiness still requires observation\n' "$release_id"
