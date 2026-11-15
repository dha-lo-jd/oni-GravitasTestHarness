using System;
using System.Collections.Generic;
using System.Linq;
using Klei.AI;

namespace Gravitas.TestHarness.Core;

/// <summary>
/// A compact picture of the colony for comparing runs at different game speeds: total mass and mean temperature of the sim grid
/// (mass should stay put), mass by element, loose items, and every duplicant's vitals. Main thread only.
/// </summary>
public static class Snapshot
{
    public static object Take()
    {
        var clock = GameClock.Instance;
        double total = 0, heat = 0;
        var byElement = new Dictionary<string, double>();
        int cells = Grid.CellCount;
        for (int i = 0; i < cells; i++)
        {
            float m = Grid.Mass[i];
            if (!(m > 0f)) continue;
            total += m;
            heat += (double)m * Grid.Temperature[i];
            var id = ElementLoader.elements[Grid.ElementIdx[i]].id.ToString();
            byElement[id] = (byElement.TryGetValue(id, out var v) ? v : 0) + m;
        }

        var dupes = new List<object>();
        foreach (var m in Components.LiveMinionIdentities.Items.Where(x => x != null))
        {
            dupes.Add(new
            {
                name = m.GetProperName(),
                cell = Grid.PosToCell(m),
                calories = Amount(Db.Get().Amounts.Calories, m),
                health = Amount(Db.Get().Amounts.HitPoints, m),
                stress = Amount(Db.Get().Amounts.Stress, m),
                breath = Amount(Db.Get().Amounts.Breath, m),
            });
        }

        double itemMass = 0;
        int items = 0;
        foreach (var p in Components.Pickupables.Items)
        {
            if (p == null) continue;
            items++;
            var pe = p.GetComponent<PrimaryElement>();
            if (pe != null) itemMass += pe.Mass;
        }

        return new
        {
            cycle = clock != null ? clock.GetCycle() : (int?)null,
            time = clock != null ? clock.GetTime() : (float?)null,
            grid_mass = Math.Round(total, 2),
            grid_mean_temperature = total > 0 ? Math.Round(heat / total, 3) : (double?)null,
            grid_mass_by_element = byElement.OrderByDescending(kv => kv.Value).Take(40)
                .ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 2)),
            duplicant_count = dupes.Count,
            duplicants = dupes,
            pickupable_count = items,
            pickupable_mass = Math.Round(itemMass, 2),
        };
    }

    private static float? Amount(Amount amount, MinionIdentity m)
    {
        try { return amount.Lookup(m)?.value; }
        catch (Exception) { return null; }
    }
}
