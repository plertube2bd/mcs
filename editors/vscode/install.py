#!/usr/bin/env python3
"""Packages this folder as a .vsix (no npm/vsce needed) and installs it into VS Code.

  python3 editors/vscode/install.py              # package + install with `code`
  python3 editors/vscode/install.py --code codium
  python3 editors/vscode/install.py --package    # only write mcs-language-<version>.vsix
"""
import json
import os
import subprocess
import sys
import zipfile
from xml.sax.saxutils import escape

HERE = os.path.dirname(os.path.abspath(__file__))
FILES = ['package.json', 'language-configuration.json', 'README.md',
         'syntaxes/mcs.tmLanguage.json', 'snippets/mcs.json', 'schemas/mcs.schema.json']


def package():
    pkg = json.load(open(os.path.join(HERE, 'package.json')))
    out = os.path.join(HERE, f"{pkg['name']}-{pkg['version']}.vsix")
    manifest = f"""<?xml version="1.0" encoding="utf-8"?>
<PackageManifest Version="2.0.0" xmlns="http://schemas.microsoft.com/developer/vsx-schema/2011">
  <Metadata>
    <Identity Language="en-US" Id="{pkg['name']}" Version="{pkg['version']}" Publisher="{pkg['publisher']}" />
    <DisplayName>{escape(pkg['displayName'])}</DisplayName>
    <Description xml:space="preserve">{escape(pkg['description'])}</Description>
    <Categories>{','.join(pkg['categories'])}</Categories>
    <Properties><Property Id="Microsoft.VisualStudio.Code.Engine" Value="{pkg['engines']['vscode']}" /></Properties>
  </Metadata>
  <Installation><InstallationTarget Id="Microsoft.VisualStudio.Code" /></Installation>
  <Dependencies />
  <Assets><Asset Type="Microsoft.VisualStudio.Code.Manifest" Path="extension/package.json" Addressable="true" /></Assets>
</PackageManifest>
"""
    types = ('<?xml version="1.0" encoding="utf-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">'
             '<Default Extension=".json" ContentType="application/json"/><Default Extension=".md" ContentType="text/markdown"/>'
             '<Default Extension=".vsixmanifest" ContentType="text/xml"/></Types>')
    with zipfile.ZipFile(out, 'w', zipfile.ZIP_DEFLATED) as z:
        z.writestr('[Content_Types].xml', types)
        z.writestr('extension.vsixmanifest', manifest)
        for f in FILES:
            z.write(os.path.join(HERE, f), 'extension/' + f)
    return out


def main():
    vsix = package()
    print(f'packaged {os.path.relpath(vsix)}')
    if '--package' in sys.argv:
        return
    code = sys.argv[sys.argv.index('--code') + 1] if '--code' in sys.argv else 'code'
    subprocess.run([code, '--install-extension', vsix, '--force'], check=True)
    print('installed — run "Developer: Reload Window" in VS Code to pick it up')


if __name__ == '__main__':
    main()
