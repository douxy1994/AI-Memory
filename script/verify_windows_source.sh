#!/bin/sh
set -eu

repo_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
windows_dir="${repo_dir}/Windows"

test -f "${windows_dir}/AIMemory.Windows.slnx"
test -f "${windows_dir}/src/AIMemory.Windows/Package.appxmanifest"
test -f "${windows_dir}/src/AIMemory.Core/Persistence/SchemaV1.sql"
test -f "${windows_dir}/parity.json"

xmllint --noout "${windows_dir}/AIMemory.Windows.slnx"
find "${windows_dir}/src/AIMemory.Windows" \
  \( -name '*.xaml' -o -name '*.appxmanifest' \) \
  -print0 | xargs -0 xmllint --noout
jq -e '.features | length > 0' "${windows_dir}/parity.json" >/dev/null

expected_tables='conversations messages approved_memories checkpoints handoff_packets agent_runs artifacts'
for table_name in ${expected_tables}; do
  rg -q "CREATE TABLE IF NOT EXISTS ${table_name}" \
    "${windows_dir}/src/AIMemory.Core/Persistence/SchemaV1.sql"
done

rg -q 'FindOrRegisterForKey' "${windows_dir}/src/AIMemory.Windows/Program.cs"
rg -q 'windows.startupTask' "${windows_dir}/src/AIMemory.Windows/Package.appxmanifest"
rg -q 'Enabled="false"' "${windows_dir}/src/AIMemory.Windows/Package.appxmanifest"
rg -q 'OrderByDescending\(value => value.detected\)' \
  "${windows_dir}/src/AIMemory.Core/Services/AgentCatalog.cs"
agent_count=$(rg -c 'new\("' \
  "${windows_dir}/src/AIMemory.Core/Services/AgentCatalog.cs")
test "${agent_count}" -eq 70

printf '%s\n' "Windows source structure verification passed."
