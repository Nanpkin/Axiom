#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
# Python is already required by cloud-setup.sh; only its standard library is used.
python3 - "${1:-tools/change-test-suites.txt}" <<'PY'
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

suites = [line.strip() for line in Path(sys.argv[1]).read_text(encoding="utf-8-sig").splitlines() if line.strip() and not line.strip().startswith("#")]
if not suites:
    sys.exit("The change test suite list is empty.")
groups = {}
seen = set()
for suite in suites:
    match = re.fullmatch(r"(Axiom\.(Domain|Engine|Providers\.Windows)\.Tests)\.([A-Za-z_][A-Za-z0-9_]*)(?:\.([A-Za-z_][A-Za-z0-9_]*))?", suite)
    if not match:
        sys.exit(f"Invalid test selection: {suite}")
    if suite in seen:
        sys.exit(f"Duplicate test selection: {suite}")
    seen.add(suite)
    groups.setdefault(match[1], []).append((suite, f"{match[1]}.{match[3]}", match[4]))

result_directory = Path("artifacts/test-results/focused").resolve()
for project, expected in groups.items():
    report = result_directory / f"{project}.trx"
    report.unlink(missing_ok=True)
    print(f"Related tests: {', '.join(name for name, _, _ in expected)}", flush=True)
    subprocess.run(["dotnet", "test", f"tests/{project}/{project}.csproj", "-c", "Release",
                    "-p:RestoreLockedMode=true", "-p:NuGetAudit=false", "/m:1", "/nr:false",
                    "--filter", "|".join(f"FullyQualifiedName={name}" if method else f"FullyQualifiedName~{cls}." for name, cls, method in expected),
                    "--logger", f"trx;LogFileName={project}.trx", "--results-directory", str(result_directory)], check=True)
    root = ET.parse(report).getroot()
    ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
    definitions = {}
    for test in root.findall("t:TestDefinitions/t:UnitTest", ns):
        method = test.find("t:TestMethod", ns).attrib
        cls = method["className"].split(",")[0].strip()
        definitions[test.attrib["id"]] = (cls, f"{cls}.{method['name']}")
    executed = {name for result in root.findall("t:Results/t:UnitTestResult", ns)
                if result.attrib.get("outcome") in ("Passed", "Failed")
                for name in definitions.get(result.attrib["testId"], ())}
    for suite, _, _ in expected:
        if suite not in executed:
            sys.exit(f"No executed tests for declared selection: {suite}")
PY
