"""Optional local verification against dt's parser; requires private fixtures."""
import argparse
import json
import sys
from pathlib import Path


def normalize(document, ignore_guids=False):
    # JSON normalizes the reference parser's vector tuples into lists.
    result = json.loads(json.dumps(document))
    if ignore_guids:
        for entry in result["Nodes"] + result["Commands"]:
            entry.pop("GUID", None)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("fixtures", type=Path)
    parser.add_argument("reference", type=Path, help="Local dt-12345/ainb source checkout")
    args = parser.parse_args()
    sys.path.insert(0, str(args.reference.resolve()))
    import ainb

    passed = failed = 0
    for binary in sorted(args.fixtures.rglob("*.ainb")):
        merged = binary.name == "csharp-merged.ainb"
        if not merged and not binary.name.endswith(".native.ainb"):
            continue
        expected_path = binary.with_name("expected.json" if merged else binary.name.replace(".native.ainb", ".json"))
        expected = json.loads(expected_path.read_text(encoding="utf-8"))
        actual = ainb.AINB.from_binary(binary.read_bytes()).as_dict()
        if normalize(actual, merged) != normalize(expected, merged):
            print("FAIL", binary)
            failed += 1
        else:
            passed += 1
    print(f"Independent dt verification: {passed} passed; {failed} failed")
    return int(failed > 0 or passed == 0)


if __name__ == "__main__":
    sys.exit(main())
