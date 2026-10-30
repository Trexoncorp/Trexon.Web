#!/usr/bin/env python3
"""Check local documentation links and compile marked, complete C# examples without executing them."""
from pathlib import Path
import re
import subprocess
import tempfile
import urllib.parse
from xml.sax.saxutils import escape

ROOT = Path(__file__).resolve().parents[1]
LINK = re.compile(r'(?<!!)\[[^\]]*\]\(([^)]+)\)')
SNIPPET = re.compile(r'<!--\s*verify:\s*([a-z0-9-]+)\s*-->\s*```csharp\s*\n(.*?)```', re.S)

def main():
    snippets = {}
    errors = []
    documents = [*ROOT.glob('README*.md'), *ROOT.glob('CHANGELOG.md'), *ROOT.glob('docs/**/*.md'), *ROOT.glob('examples/**/*.md')]
    for path in documents:
        content = path.read_text(encoding='utf-8')
        for raw in LINK.findall(content):
            target = raw.strip().strip('<>')
            if not target or target.startswith('#') or urllib.parse.urlparse(target).scheme:
                continue
            target = urllib.parse.unquote(target.split('#')[0])
            if not (path.parent / target).exists():
                errors.append(f'{path.relative_to(ROOT)}: missing link {raw}')
        for name, code in SNIPPET.findall(content):
            if name in snippets and snippets[name] != code:
                errors.append(f'{path.relative_to(ROOT)}: conflicting snippet ID {name}')
            snippets[name] = code
    if errors:
        raise SystemExit('\n'.join(errors))
    reference = escape(str(ROOT / 'Trexon.Web/Trexon.Web.csproj'), {'"': '&quot;'})
    for name, code in snippets.items():
        with tempfile.TemporaryDirectory(prefix='trexon-web-doc-') as directory:
            folder = Path(directory)
            (folder / 'Example.csproj').write_text(f'''<Project Sdk="Microsoft.NET.Sdk.Web">
<PropertyGroup><TargetFramework>net8.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
<ItemGroup><ProjectReference Include="{reference}" /></ItemGroup></Project>''')
            (folder / 'Program.cs').write_text(code)
            # Run from the repository so its global.json selects the supported SDK/test runner.
            result = subprocess.run(['dotnet', 'build', str(folder / 'Example.csproj'), '--configuration', 'Release', '-warnaserror', '--nologo'],
                                    cwd=ROOT, capture_output=True, text=True, timeout=120)
            if result.returncode:
                raise SystemExit(f'Snippet {name} failed:\n{result.stdout}\n{result.stderr}')
        print(f'Compiled documentation example: {name}')
    print(f'Documentation passed: {len(documents)} files, local links, {len(snippets)} unique complete C# examples.')

if __name__ == '__main__':
    main()
