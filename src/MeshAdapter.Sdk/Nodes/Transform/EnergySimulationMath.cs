using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
/// Pure, deterministic math of <c>SimulateEnergyMeasurements@2</c> (AB#5631): the calibrated PV
/// curve and the seeded per-member spread. The load-profile weights stay in the SDK
/// (<c>EnergyProfiles.LoadProfileWeights</c>); only what the SDK cannot express lives here.
/// </summary>
internal static class EnergySimulationMath
{
    public const int SlotsPerDay = 96;

    /// <summary>Default specific PV yield in kWh per kWp and year.</summary>
    public const double DefaultPvSpecificYield = 1050.0;

    private static readonly ConcurrentDictionary<double, double> AnnualShapeSums = new();

    /// <summary>
    /// Energy share of one 15-min slot of a PV installation with peak power 1, in kWh per kWp:
    /// a cosine bell over the daylight hours of the day (12 h plus or minus 4 h over the year, as in
    /// <c>EnergyProfiles</c>), centred on <paramref name="solarNoonUtcHour"/> and evaluated at the
    /// slot's midpoint.
    /// </summary>
    public static double PvShape(int dayOfYear, int slot, double solarNoonUtcHour)
    {
        var seasonRad = 2.0 * Math.PI * (dayOfYear - 172) / 365.25;
        var daylightHours = 12.0 + 4.0 * Math.Cos(seasonRad);
        var hourOffset = (slot + 0.5) / 4.0 - solarNoonUtcHour;
        if (Math.Abs(hourOffset) >= daylightHours / 2.0)
        {
            return 0.0;
        }

        var power = Math.Cos(Math.PI * hourOffset / daylightHours);
        return power <= 0 ? 0.0 : power * 0.25;
    }

    /// <summary>
    /// Calibration constant K: the sum of <see cref="PvShape"/> over a 365-day year. Dividing by it
    /// makes one kWp yield exactly the configured specific yield over days 1 to 365. Cached per noon.
    /// </summary>
    public static double AnnualPvShapeSum(double solarNoonUtcHour) =>
        AnnualShapeSums.GetOrAdd(solarNoonUtcHour, noon =>
        {
            var sum = 0.0;
            for (var day = 1; day <= 365; day++)
            {
                for (var slot = 0; slot < SlotsPerDay; slot++)
                {
                    sum += PvShape(day, slot, noon);
                }
            }

            return sum;
        });

    /// <summary>Calibrated PV energy in kWh of one slot.</summary>
    public static double PvSlotKWh(double kWp, int dayOfYear, int slot, double specificYield,
        double solarNoonUtcHour) =>
        kWp * PvShape(dayOfYear, slot, solarNoonUtcHour) * specificYield / AnnualPvShapeSum(solarNoonUtcHour);

    /// <summary>
    /// Deterministic size factor <c>1 + spread * (2u - 1)</c> of one member, where u in [0, 1) is
    /// derived from SHA-256 of the seed and the metering point NUMBER. Never derived from an rtId,
    /// so a rebuild that mints new rtIds reproduces the same values (SIM-03). A spread of 0 yields
    /// exactly 1.
    /// </summary>
    public static double MemberSpreadFactor(int seed, string meteringPointNumber, double spread)
    {
        if (spread == 0)
        {
            return 1.0;
        }

        return 1.0 + spread * (2.0 * UnitInterval($"{seed}|{meteringPointNumber}|size") - 1.0);
    }

    /// <summary>Uniform value in [0, 1) from the first 53 bits of SHA-256 of <paramref name="key"/>.</summary>
    public static double UnitInterval(string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        var bits = BinaryPrimitives.ReadUInt64LittleEndian(hash) >> 11;
        return bits / (double)(1UL << 53);
    }

    /// <summary>Rounds an energy value to 6 decimals, the precision every datapoint is written with.</summary>
    public static double Round(double value) => Math.Round(value, 6, MidpointRounding.AwayFromZero);
}
