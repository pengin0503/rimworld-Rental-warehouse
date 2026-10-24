using System;
using System.Collections.Generic;
using System.Xml;
using RimWorld;

namespace Verse
{
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = true)]
    public class LoadAliasAttribute : Attribute
    {
        public string alias;

        public LoadAliasAttribute(string alias)
        {
            this.alias = alias;
        }
    }

    public class Def
    {
        public string defName;
        public string label;
        public string description;
    }

    public enum Passability { Standable, PassThroughOnly, Impassable }

    public class ThingDef : Def
    {
        public Type thingClass;
        public Passability passability;
        public GraphicData graphicData;
        public List<CompProperties> comps;
        public List<ThingDefCountClass> costList;
        public List<ResearchProjectDef> researchPrerequisites;
        public BuildingProperties building;
        public bool stealable = true;
        private bool canOverlapZones;
    }

    public struct IntVec2
    {
        public int x;
        public int z;
    }

    public class SlateRef<T>
    {
    }

    public class QuestNodeDef : Def
    {
        public SlateRef<List<string>> nodes;
        public IntVec2? size;
    }

    public class GraphicData
    {
        public string texPath;
        public Type graphicClass;
    }

    public class CompProperties
    {
        public Type compClass;
    }

    public class CompProperties_Forbiddable : CompProperties
    {
    }

    public class ThingDefCountClass
    {
        public ThingDef thingDef;
        public int count;

        public void LoadDataFromXmlCustom(XmlNode node)
        {
        }
    }

    public class ResearchProjectDef : Def
    {
    }

    public class ThingWithComps
    {
    }

    public class Building : ThingWithComps
    {
    }
}

namespace RimWorld
{
    using Verse;

    public enum StoragePriority { Unstored, Low, Normal, Preferred, Important, Critical }

    public class BuildingProperties
    {
        public int maxItemsInCell;
        public StorageSettings defaultStorageSettings;
    }

    public class StorageSettings
    {
        public ThingFilter filter;

        [LoadAlias("priority")]
        private StoragePriority priorityInt;
    }

    public class ThingFilter
    {
        private List<string> categories;
        private List<string> specialFiltersToDisallow;
        private List<SpecialThingFilterDef> disallowedSpecialFilters;
    }

    public class SpecialThingFilterDef : Def
    {
        public Type workerClass;
    }
}
