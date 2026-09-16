# Regenerates src/Unison.Uwp/Assets/Fonts/UnisonIcons.ttf
# Requires: Python 3 + pip install fonttools
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
python "$here\generate_unison_icons.py"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
