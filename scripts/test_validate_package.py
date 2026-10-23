"""Tests for validate-package.py. Run: python -m unittest discover -s scripts -p "test_*.py"."""
import importlib.util
import os
import tempfile
import unittest
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
SPEC = importlib.util.spec_from_file_location("validate_package", os.path.join(HERE, "validate-package.py"))
vp = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(vp)

EN = """<?xml version="1.0" encoding="utf-8"?>
<LanguageData>
  <RW_Hello>Hello {0}</RW_Hello>
  <RW_Kind_Normal>Normal</RW_Kind_Normal>
</LanguageData>
"""
JA = """<?xml version="1.0" encoding="utf-8"?>
<LanguageData>
  <RW_Hello>こんにちは{0}</RW_Hello>
  <RW_Kind_Normal>通常</RW_Kind_Normal>
</LanguageData>
"""
DEFS = """<?xml version="1.0" encoding="utf-8"?>
<Defs>
  <ThingDef><defName>RW_Box</defName><graphicData><texPath>Things/Building/Box</texPath><graphicClass>Graphic_Single</graphicClass></graphicData></ThingDef>
  <ThingDef><defName>RW_Shelf</defName><graphicData><texPath>Things/Building/Shelf</texPath><graphicClass>Graphic_Multi</graphicClass></graphicData></ThingDef>
</Defs>
"""
SOURCE = 'class X { void F() { "RW_Hello".Translate(); ("RW_Kind_" + k).Translate(); } }'


def good_files():
    root = "RentalWarehouse/"
    return {
        root + "About/About.xml": "<ModMetaData><packageId>a.b</packageId></ModMetaData>",
        root + "LoadFolders.xml": "<loadFolders><v1.7><li>/</li></v1.7></loadFolders>",
        root + "1.7/Assemblies/RentalWarehouse.dll": b"MZ",
        root + "1.7/Assemblies/RentalWarehouse.Core.dll": b"MZ",
        root + "Defs/Things.xml": DEFS,
        root + "Languages/English/Keyed/RentalWarehouse.xml": EN,
        root + "Languages/Japanese/Keyed/RentalWarehouse.xml": JA,
        root + "Textures/Things/Building/Box.png": b"\x89PNG",
        root + "Textures/Things/Building/Shelf_north.png": b"\x89PNG",
        root + "Textures/Things/Building/Shelf_east.png": b"\x89PNG",
        root + "Textures/Things/Building/Shelf_south.png": b"\x89PNG",
    }


class ValidatePackageTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.src = os.path.join(self.tmp.name, "Source")
        os.makedirs(self.src)
        with open(os.path.join(self.src, "X.cs"), "w", encoding="utf-8") as f:
            f.write(SOURCE)

    def tearDown(self):
        self.tmp.cleanup()

    def make_zip(self, files):
        path = os.path.join(self.tmp.name, "pkg.zip")
        with zipfile.ZipFile(path, "w") as z:
            for name, data in files.items():
                z.writestr(name, data)
        return path

    def errors(self, files, with_source=True):
        return vp.validate_zip(self.make_zip(files), self.src if with_source else None)

    def test_good_package_passes(self):
        self.assertEqual([], self.errors(good_files()))

    def test_game_assembly_is_rejected(self):
        files = good_files()
        files["RentalWarehouse/1.7/Assemblies/Assembly-CSharp.dll"] = b"MZ"
        self.assertTrue(any("game assembly" in e for e in self.errors(files)))

    def test_harmony_and_waylith_assemblies_are_rejected(self):
        for name in ("0Harmony.dll", "Waylith.dll", "UnityEngine.CoreModule.dll"):
            files = good_files()
            files["RentalWarehouse/1.7/Assemblies/" + name] = b"MZ"
            self.assertTrue(any("game assembly" in e for e in self.errors(files)), name)

    def test_build_intermediates_are_rejected(self):
        for name in ("obj/project.assets.json", "1.7/Assemblies/RentalWarehouse.pdb", "Source/X.cs", "RentalWarehouse.csproj", "1.7/Assemblies/RentalWarehouse.Game.Tests.dll"):
            files = good_files()
            files["RentalWarehouse/" + name] = b"x"
            self.assertTrue(any("development file" in e for e in self.errors(files)), name)

    def test_secrets_are_rejected(self):
        files = good_files()
        files["RentalWarehouse/notes.txt"] = "token ghp_abcdefghijklmnopqrstuvwxyz0123456789"
        self.assertTrue(any("secret" in e for e in self.errors(files)))

    def test_missing_required_file_is_reported(self):
        files = good_files()
        del files["RentalWarehouse/1.7/Assemblies/RentalWarehouse.Core.dll"]
        self.assertTrue(any("missing required" in e for e in self.errors(files)))

    def test_language_key_missing_in_japanese_is_reported(self):
        files = good_files()
        files["RentalWarehouse/Languages/Japanese/Keyed/RentalWarehouse.xml"] = JA.replace("<RW_Hello>こんにちは{0}</RW_Hello>", "")
        self.assertTrue(any("RW_Hello" in e and "Japanese" in e for e in self.errors(files)))

    def test_placeholder_mismatch_is_reported(self):
        files = good_files()
        files["RentalWarehouse/Languages/Japanese/Keyed/RentalWarehouse.xml"] = JA.replace("こんにちは{0}", "こんにちは")
        self.assertTrue(any("placeholder" in e for e in self.errors(files)))

    def test_key_used_in_code_but_untranslated_is_reported(self):
        with open(os.path.join(self.src, "Y.cs"), "w", encoding="utf-8") as f:
            f.write('var s = "RW_Missing".Translate();')
        self.assertTrue(any("RW_Missing" in e for e in self.errors(good_files())))

    def test_dynamic_prefix_without_any_key_is_reported(self):
        with open(os.path.join(self.src, "Y.cs"), "w", encoding="utf-8") as f:
            f.write('var s = ("RW_Phase_" + p).Translate();')
        self.assertTrue(any("RW_Phase_" in e for e in self.errors(good_files())))

    def test_missing_texture_is_reported(self):
        files = good_files()
        del files["RentalWarehouse/Textures/Things/Building/Shelf_east.png"]
        self.assertTrue(any("Shelf_east" in e for e in self.errors(files)))

    def test_malformed_xml_is_reported(self):
        files = good_files()
        files["RentalWarehouse/Defs/Broken.xml"] = "<Defs><ThingDef></Defs>"
        self.assertTrue(any("not well-formed" in e for e in self.errors(files)))


if __name__ == "__main__":
    unittest.main()
