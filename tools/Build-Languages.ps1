param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'

$languages = @(
    @{ File = 'english.csv'; Name = 'English'; LangId = 1033; Output = 'English.xml' },
    @{ File = 'spanish.csv'; Name = ('Espa' + [char]0x00F1 + 'ol'); LangId = 3082; Output = 'Spanish.xml' },
    @{ File = 'french.csv'; Name = ('Fran' + [char]0x00E7 + 'ais'); LangId = 1036; Output = 'French.xml' },
    @{ File = 'german.csv'; Name = 'Deutsch'; LangId = 1031; Output = 'German.xml' },
    @{ File = 'italian.csv'; Name = 'Italiano'; LangId = 1040; Output = 'Italian.xml' },
    @{ File = 'portuguese.csv'; Name = ('Portugu' + [char]0x00EA + 's (Brasil)'); LangId = 1046; Output = 'Portuguese.xml' }
)

$outputDir = Join-Path $Root 'src\CleanRift\Languages'
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null

function Add-Node {
    param(
        [System.Xml.XmlDocument]$Document,
        [System.Xml.XmlElement]$Parent,
        [System.Collections.Specialized.OrderedDictionary]$Tree
    )

    foreach ($name in $Tree.Keys) {
        $element = $Document.CreateElement([string]$name)
        $value = $Tree[$name]
        if ($value -is [System.Collections.Specialized.OrderedDictionary]) {
            $Parent.AppendChild($element) | Out-Null
            Add-Node -Document $Document -Parent $element -Tree $value
        }
        else {
            $element.InnerText = [string]$value
            $Parent.AppendChild($element) | Out-Null
        }
    }
}

foreach ($language in $languages) {
    $source = Join-Path $Root ('languages\' + $language.File)
    $rows = Import-Csv $source -Encoding UTF8
    if ($rows.Count -eq 0) { throw "No rows in $source" }

    $tree = [ordered]@{}
    $seen = @{}
    foreach ($row in $rows) {
        $key = $row.Key.Trim()
        if ([string]::IsNullOrWhiteSpace($key)) { throw "Empty key in $source" }
        if ($seen.ContainsKey($key)) { throw "Duplicate key '$key' in $source" }
        $seen[$key] = $true

        $segments = $key.Split('/')
        $node = $tree
        for ($i = 0; $i -lt $segments.Length - 1; $i++) {
            $segment = $segments[$i]
            if (-not $node.Contains($segment)) {
                $node[$segment] = [ordered]@{}
            }
            $node = $node[$segment]
        }
        $node[$segments[$segments.Length - 1]] = [string]$row.Text
    }

    $document = New-Object System.Xml.XmlDocument
    $declaration = $document.CreateXmlDeclaration('1.0', 'UTF-8', $null)
    $document.AppendChild($declaration) | Out-Null

    $rootElement = $document.CreateElement('Language')
    $rootElement.SetAttribute('Name', $language.Name)
    $rootElement.SetAttribute('LangID', [string]$language.LangId)
    $rootElement.SetAttribute('Version', '1')
    $document.AppendChild($rootElement) | Out-Null

    Add-Node -Document $document -Parent $rootElement -Tree $tree

    $target = Join-Path $outputDir $language.Output
    $settings = New-Object System.Xml.XmlWriterSettings
    $settings.Encoding = New-Object System.Text.UTF8Encoding($false)
    $settings.Indent = $true
    $settings.IndentChars = '    '
    $settings.NewLineChars = "`r`n"
    $settings.NewLineHandling = [System.Xml.NewLineHandling]::Replace
    $writer = [System.Xml.XmlWriter]::Create($target, $settings)
    try {
        $document.Save($writer)
    }
    finally {
        $writer.Dispose()
    }

    Write-Host ("{0}: {1} keys -> {2}" -f $language.Name, $rows.Count, $target)
}
