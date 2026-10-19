# Regenerates src/Unison.Uwp/Assets/Fonts/WhatsAppLegacyIcons.ttf
# Requires: Python 3 + pip install fonttools pillow numpy scikit-image
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
python "$here\generate_whatsapp_legacy_icons.py"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
