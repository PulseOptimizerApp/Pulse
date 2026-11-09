using Pulse.Core.Games.Valorant;
using Xunit;

namespace PulseOptimizer.Tests;

public class AbilityWeaponTests
{
    [Fact]
    public void Chamber_Neon_Jett_and_Raze_kills_get_named_weapons()
    {
        var m = new VMatch { Id = "aw", Me = "me", Queue = "competitive" };
        m.Players.Add(new VPlayer { Id = "me", Team = "Red", Agent = "Chamber" });
        m.Players.Add(new VPlayer { Id = "j", Team = "Red", Agent = "Jett" });
        m.Players.Add(new VPlayer { Id = "e", Team = "Blue", Agent = "Sage" });
        m.Kills.Add(new VKill { Killer = "me", Victim = "e", DamageType = "Weapon", Weapon = "856D9A7E-4B06-DC37-15DC-9D809C37CB90" }); // Headhunter (Riot sends uppercase)
        m.Kills.Add(new VKill { Killer = "me", Victim = "e", DamageType = "Weapon", Weapon = "" });                                      // old Tour De Force kills: no id
        m.Kills.Add(new VKill { Killer = "j", Victim = "e", DamageType = "Ultimate", Weapon = "" });                                     // Blade Storm
        m.NameAbilityWeapons();

        Assert.Equal("Headhunter", MapGeo.Weapon(m.Kills[0].Weapon));
        Assert.Equal(VMatch.TourDeForce, m.Kills[1].Weapon);
        Assert.Equal("Tour De Force", MapGeo.Weapon(m.Kills[1].Weapon));
        Assert.Equal("Weapon", m.Kills[2].DamageType);
        Assert.Equal("Blade Storm", MapGeo.Weapon(m.Kills[2].Weapon));
        Assert.Equal("Ability", MapGeo.WeaponCategory(VMatch.Overdrive));

        var weapons = Panels.Weapons(new[] { m }, "me");
        Assert.Contains(weapons, w => w.Name == "Headhunter" && w.Kills == 1);
        Assert.Contains(weapons, w => w.Name == "Tour De Force" && w.Kills == 1);
        Assert.DoesNotContain(weapons, w => w.Name == "Weapon");
    }
}
