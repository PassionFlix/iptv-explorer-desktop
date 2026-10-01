function Assert-SupportedSemanticVersion {
    param([Parameter(Mandatory)][string]$Version)

    if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') {
        throw "Version SemVer non prise en charge : $Version"
    }
}

function Get-ProjectVersion {
    param([Parameter(Mandatory)][string]$PropsPath)

    [xml]$props = Get-Content -LiteralPath $PropsPath -Raw
    $version = [string]$props.Project.PropertyGroup.Version
    if ([string]::IsNullOrWhiteSpace($version)) { throw 'Directory.Build.props ne contient aucune version.' }
    Assert-SupportedSemanticVersion -Version $version

    if ([string]$props.Project.PropertyGroup.AssemblyVersion -ne '$(Version).0' -or
        [string]$props.Project.PropertyGroup.FileVersion -ne '$(Version).0' -or
        [string]$props.Project.PropertyGroup.InformationalVersion -ne '$(Version)') {
        throw 'Les versions assembly, fichier et informationnelle doivent être dérivées de $(Version).'
    }

    return $version
}

function Assert-RequestedProjectVersion {
    param(
        [Parameter(Mandatory)][string]$RequestedVersion,
        [Parameter(Mandatory)][string]$ProjectVersion
    )

    Assert-SupportedSemanticVersion -Version $RequestedVersion
    if ($RequestedVersion -ne $ProjectVersion) {
        throw "La version demandée ($RequestedVersion) ne correspond pas à Directory.Build.props ($ProjectVersion)."
    }
}
