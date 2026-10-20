# Builds the dashboard UI and puts it where the runner serves it (src/AiRunner/wwwroot, copied next to the binaries
# on the next dotnet build). Steps: check Node, write the API's OpenAPI document (runner --openapi: no repos.yml,
# no token, no GitBucket), npm ci, regenerate the client in src/app/api, unit tests, production build, copy.
# dotnet build and dotnet test never call this; run it after a UI or API change, then restart the runner.
$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$dashboard = Join-Path $root 'src/AiRunner.Dashboard'
$runner = Join-Path $root 'src/AiRunner'
$webRoot = Join-Path $runner 'wwwroot'
$openApiFile = Join-Path $dashboard 'openapi.json'

function Invoke-Step([string] $name, [scriptblock] $command) {
    Write-Host "==> $name" -ForegroundColor Cyan
    & $command
    if ($LASTEXITCODE -ne 0) {
        throw "$name failed (exit code $LASTEXITCODE)."
    }
}

# The supported range is "engines" in package.json; .npmrc (engine-strict) makes npm ci stop with a clear message
# when Node is outside it, so the range lives in one place.
$requiredRange = (Get-Content (Join-Path $dashboard 'package.json') -Raw | ConvertFrom-Json).engines.node
Write-Host "Node $(node --version) (the dashboard needs $requiredRange)"

# An absolute path: dotnet run starts the runner in its project folder.
Invoke-Step 'Write the OpenAPI document' { dotnet run --project $runner -- --openapi $openApiFile }

Push-Location $dashboard
try {
    Invoke-Step 'npm ci' { npm ci }
    Invoke-Step 'Generate the API client' { npm run generate:api }
    Invoke-Step 'Unit tests' { npm test }
    Invoke-Step 'Production build' { npm run build }
}
finally {
    Pop-Location
}

$buildOutput = Join-Path $dashboard 'dist/ai-runner-dashboard/browser'
Write-Host "==> Copy to $webRoot" -ForegroundColor Cyan
if (Test-Path $webRoot) {
    Remove-Item $webRoot -Recurse -Force
}

Copy-Item $buildOutput $webRoot -Recurse

Write-Host ''
Write-Host 'Dashboard built. Restart the runner with dashboard.enabled: true in repos.yml.' -ForegroundColor Green
Write-Host 'If src/AiRunner.Dashboard/src/app/api changed, commit it with the API change.'
