using System.Collections.Generic;
using System.Globalization;

namespace RentalWarehouse.Core
{
    /// <summary>
    /// Return pods (spec 5): one at a time, re-requestable after loss without extending the grace period. Crates count
    /// as returned only when the pod is dispatched, never when merely loaded.
    /// </summary>
    public static class ReturnService
    {
        public static Decision<ContractState> RequestPod(ContractState state, long now)
        {
            if (state.Phase != ContractPhase.Returning) return Decision<ContractState>.Fail("RW_Reject_NotReturning", state);
            if (state.ActiveReturnPodId != null) return Decision<ContractState>.Fail("RW_Reject_PodAlreadyActive", state);
            state.ReturnPodsRequested++;
            state.ActiveReturnPodId = ContractEngine.Id(state, "pod", state.ReturnPodsRequested.ToString(CultureInfo.InvariantCulture));
            var effect = new EffectRequest
            {
                EffectId = state.ActiveReturnPodId,
                Kind = EffectKind.CreateReturnPod,
                EntityId = state.Terms.ContractId,
                Payload = { ["map"] = state.MapId },
            };
            return Decision<ContractState>.Ok(state, new List<EffectRequest> { effect });
        }

        public static void NotifyPodLost(ContractState state, string podId)
        {
            if (state.ActiveReturnPodId == podId) state.ActiveReturnPodId = null;
        }

        public static Decision<ContractState> Dispatch(ContractState state, IReadOnlyList<string> cargoIds, long now)
        {
            if (state.Phase != ContractPhase.Returning) return Decision<ContractState>.Fail("RW_Reject_NotReturning", state);
            if (state.ActiveReturnPodId == null) return Decision<ContractState>.Fail("RW_Reject_NoReturnPod", state);
            state.ActiveReturnPodId = null;
            var effects = new List<EffectRequest>();
            foreach (var id in cargoIds)
            {
                var cs = state.Cargo(id);
                if (cs == null || cs.Returned || cs.Loss != LossCause.None) continue;
                effects.AddRange(ContractEngine.MarkReturned(state, id, now).Effects);
                if (state.IsTerminal) break;
            }
            return Decision<ContractState>.Ok(state, effects);
        }
    }
}
