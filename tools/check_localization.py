"""Keep interface translations complete; EPUB content is outside this catalog."""

import json
from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
CJK = re.compile(r"[\u3400-\u9fff]")
STRING = r'"(?:\\.|[^"\\])*"'


def main():
    catalog = json.loads((ROOT / "src/QuietRead.Core/Interface.en.json").read_text())
    errors = []
    for source, english in catalog.items():
        if not source or not english:
            errors.append("Empty localization entry")
    for path in (ROOT / "src/QuietRead.App").glob("*.xaml"):
        for element in ET.parse(path).iter():
            for name, value in element.attrib.items():
                if value.startswith("{local:Localize Key="):
                    source = value[len("{local:Localize Key="):-1].strip("'")
                    if source not in catalog:
                        errors.append(f"Missing translation: {path.name}: {source}")
                elif name.rsplit("}", 1)[-1] in {"Text", "Content", "Header", "ToolTip", "AutomationProperties.Name", "Title"} and CJK.search(value):
                    if value != "简体中文":  # Native language names stay recognizable.
                        errors.append(f"Unlocalized XAML text: {path.name}: {value}")
    for path in (ROOT / "src/QuietRead.App").glob("*.cs"):
        text = path.read_text()
        # Fold adjacent literal strings, such as the multiline help message.
        text = re.sub(STRING + r'(?:\s*\+\s*' + STRING + r')+',
                      lambda m: json.dumps("".join(json.loads(s[0]) for s in re.finditer(STRING, m[0])), ensure_ascii=False), text)
        for match in re.finditer(STRING, text):
            source = json.loads(match[0])
            if CJK.search(source) and source not in catalog:
                if source in {"请选择 ", " 文件。"}:  # LocalFilePath error template, covered by Error().
                    continue
                errors.append(f"Missing C# translation: {path.name}: {source}")
    if errors:
        print("\n".join(errors), file=sys.stderr)
        return 1
    print(f"PASS: {len(catalog)} bilingual messages; XAML and application text coverage.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
