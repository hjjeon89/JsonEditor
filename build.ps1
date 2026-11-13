#Requires -Version 5.1
<#
.SYNOPSIS
Publishes JsonEditor and its runtime to the project's build folder.
#>
[CmdletBinding(SupportsShouldProcess)]
param()

& (Join-Path $PSScriptRoot 'install.ps1') -BuildOnly -InstallDirectory (Join-Path $PSScriptRoot 'build/Release') @PSBoundParameters
