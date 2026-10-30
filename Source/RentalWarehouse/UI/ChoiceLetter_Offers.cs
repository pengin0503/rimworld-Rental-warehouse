using System.Collections.Generic;
using Verse;

namespace RentalWarehouse
{
    /// <summary>The new-offers letter: one choice opens the offer list, the other closes the letter.</summary>
    public class ChoiceLetter_Offers : ChoiceLetter
    {
        public override IEnumerable<DiaOption> Choices
        {
            get
            {
                yield return new DiaOption("RW_Letter_OpenTab".Translate())
                {
                    action = () => WarehouseTab.Open(WarehouseTab.Page.Offers),
                    resolveTree = true,
                };
                yield return Option_Close;
            }
        }
    }
}
