using System.Collections.Generic;
using System.IO;
using System.Linq;
using RentalWarehouse.Tools;
using Xunit;

namespace RentalWarehouse.Tools.Tests
{
    public class ValidatorTests
    {
        private static readonly string FakeGame = Path.Combine(System.AppContext.BaseDirectory, "FakeGame.dll");

        private static List<string> Check(string defs, string? reference = null)
        {
            var dir = Directory.CreateTempSubdirectory();
            var file = Path.Combine(dir.FullName, "Mod.xml");
            File.WriteAllText(file, "<?xml version=\"1.0\" encoding=\"utf-8\"?><Defs>" + defs + "</Defs>");
            var refs = new List<string>();
            if (reference != null)
            {
                var r = Path.Combine(dir.FullName, "Core.xml");
                File.WriteAllText(r, "<?xml version=\"1.0\" encoding=\"utf-8\"?><Defs>" + reference + "</Defs>");
                refs.Add(r);
            }
            using var v = new DefValidator(new[] { FakeGame });
            return v.Validate(new[] { file }, reference == null ? null : refs);
        }

        private const string Core = "<ThingDef><defName>Steel</defName></ThingDef><ResearchProjectDef><defName>Machining</defName></ResearchProjectDef>"
            + "<ThingDef Name=\"BuildingBase\" Abstract=\"True\"><label>x</label></ThingDef><SpecialThingFilterDef><defName>AllowX</defName></SpecialThingFilterDef>";

        [Fact]
        public void ValidDef_HasNoIssues()
        {
            var issues = Check(@"<ThingDef ParentName=""BuildingBase""><defName>RW_A</defName><thingClass>Verse.Building</thingClass>
                <passability>PassThroughOnly</passability><graphicData><texPath>a/b</texPath></graphicData>
                <comps><li Class=""CompProperties_Forbiddable"" /></comps><costList><Steel>5</Steel></costList>
                <researchPrerequisites><li>Machining</li></researchPrerequisites><canOverlapZones>false</canOverlapZones>
                <building><maxItemsInCell>2</maxItemsInCell><defaultStorageSettings><priority>Important</priority>
                <filter><categories><li>X</li></categories><specialFiltersToDisallow><li>AllowX</li></specialFiltersToDisallow></filter>
                </defaultStorageSettings></building></ThingDef>", Core);

            Assert.Empty(issues);
        }

        [Fact]
        public void UnknownField_IsReported()
        {
            Assert.Contains(Check("<ThingDef><defName>RW_A</defName><notAField>1</notAField></ThingDef>"), i => i.Contains("notAField") && i.Contains("ThingDef"));
        }

        [Fact]
        public void CaseMismatch_IsReported()
        {
            Assert.Contains(Check("<ThingDef><defName>RW_A</defName><Label>x</Label></ThingDef>"), i => i.Contains("case"));
        }

        [Fact]
        public void UnknownOrWrongClassAttribute_IsReported()
        {
            Assert.Contains(Check("<ThingDef><defName>RW_A</defName><comps><li Class=\"Nope\" /></comps></ThingDef>"), i => i.Contains("Nope"));
            Assert.Contains(Check("<ThingDef><defName>RW_A</defName><comps><li Class=\"GraphicData\" /></comps></ThingDef>"), i => i.Contains("not a CompProperties"));
        }

        [Fact]
        public void ListItemNotLi_IsReported()
        {
            Assert.Contains(Check("<ThingDef><defName>RW_A</defName><researchPrerequisites><item>Machining</item></researchPrerequisites></ThingDef>"), i => i.Contains("<li>"));
        }

        [Fact]
        public void BadEnumValue_IsReported()
        {
            Assert.Contains(Check("<ThingDef><defName>RW_A</defName><passability>Walkable</passability></ThingDef>"), i => i.Contains("Walkable"));
        }

        [Fact]
        public void UnknownTypeName_IsReported()
        {
            Assert.Contains(Check("<ThingDef><defName>RW_A</defName><thingClass>Verse.Nope</thingClass></ThingDef>"), i => i.Contains("Verse.Nope"));
        }

        [Fact]
        public void MissingDefReference_IsReported_IncludingCustomLoadedCostList()
        {
            var issues = Check("<ThingDef><defName>RW_A</defName><costList><Unobtainium>1</Unobtainium></costList><researchPrerequisites><li>Nope</li></researchPrerequisites></ThingDef>", Core);

            Assert.Contains(issues, i => i.Contains("Unobtainium"));
            Assert.Contains(issues, i => i.Contains("Nope") && i.Contains("ResearchProjectDef"));
        }

        [Fact]
        public void ReferenceToOwnDef_Resolves()
        {
            var issues = Check("<ResearchProjectDef><defName>RW_R</defName></ResearchProjectDef><ThingDef><defName>RW_A</defName><researchPrerequisites><li>RW_R</li></researchPrerequisites></ThingDef>", Core);

            Assert.Empty(issues);
        }

        [Fact]
        public void MissingParent_IsReported()
        {
            Assert.Contains(Check("<ThingDef ParentName=\"NoSuchBase\"><defName>RW_A</defName></ThingDef>", Core), i => i.Contains("NoSuchBase"));
        }

        [Fact]
        public void DuplicateField_IsReported()
        {
            Assert.Contains(Check("<ThingDef><defName>RW_A</defName><label>a</label><label>b</label></ThingDef>"), i => i.Contains("twice"));
        }

        [Fact]
        public void UnknownDefType_IsReported()
        {
            Assert.Contains(Check("<WidgetDef><defName>RW_W</defName></WidgetDef>"), i => i.Contains("WidgetDef"));
        }

        [Fact]
        public void RuntimeOnlyFilterField_IsReported()
        {
            Assert.Contains(Check("<ThingDef><defName>RW_A</defName><building><defaultStorageSettings><filter><disallowedSpecialFilters><li>AllowX</li></disallowedSpecialFilters></filter></defaultStorageSettings></building></ThingDef>", Core),
                i => i.Contains("specialFiltersToDisallow"));
        }

        [Fact]
        public void SlateRefContent_IsNotValidatedAsFields()
        {
            Assert.Empty(Check("<QuestNodeDef><defName>Q</defName><nodes><li>a</li><li>b</li></nodes></QuestNodeDef>"));
        }

        [Fact]
        public void NullableStructWithChildren_IsValidated()
        {
            Assert.Empty(Check("<QuestNodeDef><defName>Q</defName><size><x>1</x><z>2</z></size></QuestNodeDef>"));
            Assert.Contains(Check("<QuestNodeDef><defName>Q</defName><size><y>1</y></size></QuestNodeDef>"), i => i.Contains("<y>"));
        }

        [Fact]
        public void DefTypeName_IsCaseInsensitiveLikeTheGame()
        {
            Assert.Empty(Check("<thingDef><defName>RW_A</defName></thingDef>"));
        }

        [Fact]
        public void MayRequireOnAncestor_MakesReferenceOptional()
        {
            Assert.Empty(Check("<ThingDef><defName>RW_A</defName><researchPrerequisites MayRequire=\"Some.DLC\"><li>DlcProject</li></researchPrerequisites></ThingDef>", Core));
        }

        [Fact]
        public void ImpliedDefNames_AreAccepted()
        {
            Assert.Empty(Check("<ThingDef><defName>RW_A</defName><costList><Meat_Human>1</Meat_Human></costList></ThingDef>", Core));
        }

        [Fact]
        public void ReferencesInsideAbstractDefs_AreNotResolved()
        {
            Assert.Empty(Check("<ThingDef Name=\"RW_Base\" Abstract=\"True\"><researchPrerequisites><li>DlcOnly</li></researchPrerequisites></ThingDef>", Core));
        }
    }
}
