using System.Net;
using System.Text.Json.Nodes;
using MatCMS.Services;
using MatCMS.Services.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MatCMS.Plugin.Tests;

/// <summary>
/// The booking plugin's operations for AI agents (plugins/booking/BookingOps*.cs) inside the whole application, set up like <see cref="BookingTests"/>
/// (same fixture class, its own application): every op is there with the right flags, a house with a unit, a season with its price table and a fee
/// make a round trip create → read → change → undo (the price check follows every step), bad input is refused and writes nothing, internal undo
/// helpers are refused to an outside caller, and a guest's request is read and moved along only through the booking flow.
/// </summary>
public sealed class BookingOpsTests(BookingApp app) : IClassFixture<BookingApp>
{
    private static readonly string[] Reads = { "house.list", "house.read", "unit.list", "unit.read", "season.list", "fee.list", "price.check", "stay.list", "stay.read" };
    private static readonly string[] Writes = { "house.create", "house.update", "unit.create", "unit.update", "season.create", "season.update", "season.delete", "fee.create", "fee.update", "fee.delete", "stay.move" };
    private static readonly string[] Internals = { "house.remove", "house.restore", "unit.remove", "unit.restore", "season.remove", "season.restore", "fee.remove", "fee.restore" };

    private async Task<JsonObject> RunAsync(string op, JsonObject args, bool allowInternal = false)
    {
        await app.AdminAsync();
        using var scope = app.App.Services.CreateScope();
        return (JsonObject)(await scope.ServiceProvider.GetRequiredService<PluginContributions>().RunOperationAsync(scope.ServiceProvider, op, args, default, allowInternal))!;
    }

    /// <summary>Runs the undo an answer carries, the way the assistant's "Rückgängig" does (the host may have qualified its name with the plugin's key).</summary>
    private async Task<JsonObject> UndoAsync(JsonObject answer, string expected, string status = "applied")
    {
        var undo = (JsonObject)answer["undo"]!;
        var name = undo["name"]!.GetValue<string>();
        Assert.True(name == expected || name == "booking." + expected, name);
        var result = await RunAsync(name, (JsonObject)undo["args"]!.DeepClone(), allowInternal: true);
        Assert.True(Status(result) == status, name + ": " + result.ToJsonString());
        return result;
    }

    private static string Status(JsonObject a) => a["status"]!.GetValue<string>();
    private static long Id(JsonObject a) => a["data"]!["id"]!.GetValue<long>();
    private static JsonObject Applied(JsonObject a) { Assert.True(Status(a) == "applied", a.ToJsonString()); return a; }

    private async Task<long> PriceAsync(long unit, string arrival = "2031-05-10", string departure = "2031-05-12")
    {
        var q = Applied(await RunAsync("price.check", new JsonObject { ["unit"] = unit, ["arrival"] = arrival, ["departure"] = departure, ["adults"] = 2 }));
        return q["data"]!["total_cents"]!.GetValue<long>();
    }

    [Fact]
    public async Task Every_operation_is_offered_with_its_flags_and_the_guest_ops_never_reach_the_assistant()
    {
        await app.AdminAsync();
        var contributions = app.App.Services.GetRequiredService<PluginContributions>();
        var registry = app.App.Services.GetRequiredService<PluginRegistry>();
        var ops = registry.Operations.Where(o => o.Owner == "booking").ToDictionary(o => o.Name);
        Assert.Equal(Reads.Concat(Writes).Concat(Internals).OrderBy(x => x), ops.Keys.OrderBy(x => x));
        foreach (var name in ops.Keys)
        {
            Assert.True(contributions.HasOperation(name), name);
            var spec = ops[name].Spec!;
            Assert.False(string.IsNullOrWhiteSpace(spec.Description), name);
            Assert.Equal(!Reads.Contains(name), spec.Writes);
            Assert.Equal(Internals.Contains(name), spec.Internal);
            Assert.Equal(name.StartsWith("stay.", StringComparison.Ordinal), spec.PersonalData);
        }
        var assistant = contributions.AssistantOperations.Where(o => o.Owner == "booking").Select(o => o.Name).ToList();
        Assert.DoesNotContain(assistant, n => n.StartsWith("stay.", StringComparison.Ordinal) || Internals.Contains(n));
        Assert.Contains("season.create", assistant);
    }

    [Fact]
    public async Task A_house_unit_season_and_fee_make_a_round_trip_and_every_undo_puts_it_back()
    {
        var house = Applied(await RunAsync("house.create", new JsonObject { ["name"] = "Ops Haus", ["country"] = "de", ["check_in"] = "16:00", ["directions"] = "Am Hafen links." }));
        var houseId = Id(house);
        Assert.Equal("skipped-exists", Status(await RunAsync("house.create", new JsonObject { ["name"] = "ops haus" })));
        var read = Applied(await RunAsync("house.read", new JsonObject { ["house"] = houseId }));
        Assert.Equal("DE", read["data"]!["country"]!.GetValue<string>());
        Assert.Equal("Am Hafen links.", read["data"]!["directions"]!.GetValue<string>());

        var changed = Applied(await RunAsync("house.update", new JsonObject { ["house"] = houseId, ["name"] = "Ops Haus am Meer", ["check_in"] = "14:00" }));
        Assert.Equal("Ops Haus am Meer", (await RunAsync("house.read", new JsonObject { ["house"] = houseId }))["data"]!["name"]!.GetValue<string>());
        await UndoAsync(changed, "house.restore");
        var back = (await RunAsync("house.read", new JsonObject { ["house"] = houseId }))["data"]!;
        Assert.Equal("Ops Haus", back["name"]!.GetValue<string>());
        Assert.Equal("16:00", back["check_in"]!.GetValue<string>());

        var unit = Applied(await RunAsync("unit.create", new JsonObject
        {
            ["house"] = houseId, ["name"] = "Ops Studio", ["guests_std"] = 2, ["guests_max"] = 3, ["base_price_cents"] = 5000, ["image"] = "/uploads/studio.jpg"
        }));
        var unitId = Id(unit);
        Assert.Equal("ops-studio", unit["data"]!["slug"]!.GetValue<string>());
        Assert.Equal(10000, await PriceAsync(unitId));                                              // two nights at the base price, no fees yet

        var season = Applied(await RunAsync("season.create", new JsonObject
        {
            ["house"] = houseId, ["name"] = "Mai", ["from"] = "2031-05-01", ["to"] = "2031-05-31", ["rank"] = 2,
            ["rates"] = new JsonArray(new JsonObject { ["unit"] = "ops-studio", ["price_cents"] = 9000, ["min_nights"] = 2, ["arrival_days"] = new JsonArray() })
        }));
        var seasonId = Id(season);
        Assert.Equal(18000, await PriceAsync(unitId));
        var listed = (JsonArray)Applied(await RunAsync("season.list", new JsonObject { ["house"] = houseId }))["data"]!;
        Assert.Equal(9000, listed.Single()!["rates"]![0]!["price_cents"]!.GetValue<long>());

        var dearer = Applied(await RunAsync("season.update", new JsonObject { ["season"] = seasonId, ["rates"] = new JsonArray(new JsonObject { ["unit"] = unitId, ["price_cents"] = 10000 }) }));
        Assert.Equal(20000, await PriceAsync(unitId));
        await UndoAsync(dearer, "season.restore");
        Assert.Equal(18000, await PriceAsync(unitId));

        var fee = Applied(await RunAsync("fee.create", new JsonObject { ["house"] = houseId, ["name"] = "Endreinigung", ["amount_cents"] = 3000, ["per"] = "stay" }));
        Assert.Equal(21000, await PriceAsync(unitId));
        var feeChange = Applied(await RunAsync("fee.update", new JsonObject { ["fee"] = Id(fee), ["amount_cents"] = 4000 }));
        Assert.Equal(22000, await PriceAsync(unitId));
        await UndoAsync(feeChange, "fee.restore");
        Assert.Equal(21000, await PriceAsync(unitId));

        // switched off rather than deleted — and switched on again by the undo
        var off = Applied(await RunAsync("unit.update", new JsonObject { ["unit"] = unitId, ["active"] = false }));
        Assert.Equal("failed", Status(await RunAsync("price.check", new JsonObject { ["unit"] = unitId, ["arrival"] = "2031-05-10", ["departure"] = "2031-05-12" })));
        await UndoAsync(off, "unit.restore");
        Assert.Equal(21000, await PriceAsync(unitId));

        // a deleted season comes back with its prices
        var deleted = Applied(await RunAsync("season.delete", new JsonObject { ["season"] = seasonId }));
        Assert.Equal(13000, await PriceAsync(unitId));
        await UndoAsync(deleted, "season.restore");
        Assert.Equal(21000, await PriceAsync(unitId));

        // the undos of the creates, newest first, leave nothing behind
        await UndoAsync(fee, "fee.remove");
        Assert.Equal(18000, await PriceAsync(unitId));
        // the season's first row was deleted and came back as a new one: its undo is answered, not thrown — the house's removal takes the new row along
        Assert.Equal("skipped", Status(await UndoAsync(season, "season.remove", "skipped")));
        Assert.Equal("failed", Status(await UndoAsync(house, "house.remove", "failed")));             // the unit still stands
        await UndoAsync(unit, "unit.remove");
        await UndoAsync(house, "house.remove");
        Assert.DoesNotContain((JsonArray)(await RunAsync("season.list", new JsonObject()))["data"]!, s => s!["house"]!.GetValue<long>() == houseId);
        var houses = (JsonArray)Applied(await RunAsync("house.list", new JsonObject()))["data"]!;
        Assert.DoesNotContain(houses, h => h!["id"]!.GetValue<long>() == houseId);
    }

    [Fact]
    public async Task Bad_input_is_refused_and_writes_nothing()
    {
        await app.AdminAsync();
        var house = app.Ids["house"];
        async Task Refused(string op, JsonObject args, string because)
        {
            var a = await RunAsync(op, args);
            Assert.True(Status(a) == "failed", op + ": " + a.ToJsonString());
            Assert.Contains(because, a["message"]!.GetValue<string>());
        }
        async Task<int> Seasons() => ((JsonArray)(await RunAsync("season.list", new JsonObject { ["house"] = house }))["data"]!).Count;
        var before = await Seasons();

        await Refused("unit.create", new JsonObject { ["house"] = house, ["name"] = "Böse", ["image"] = "javascript:alert(1)" }, "image");
        await Refused("unit.create", new JsonObject { ["house"] = house, ["name"] = "Billig", ["base_price_cents"] = -100 }, "negativ");
        await Refused("unit.create", new JsonObject { ["house"] = house, ["name"] = "Halb", ["base_price_cents"] = "zwölf" }, "ganze Zahl");
        await Refused("unit.update", new JsonObject { ["unit"] = app.Ids["a1"], ["contains"] = new JsonArray(app.Ids["all"]) }, "Teil eines ganzen Hauses");
        await Refused("house.create", new JsonObject { ["name"] = "Nirgendwo", ["country"] = "XX" }, "country");
        await Refused("season.create", new JsonObject
        {
            ["house"] = house, ["name"] = "Kaputt", ["from"] = "2032-01-01", ["to"] = "2032-01-31",
            ["rates"] = new JsonArray(new JsonObject { ["unit"] = app.Ids["a1"], ["price_cents"] = 1000, ["arrival_days"] = new JsonArray(9) })
        }, "arrival_days");
        await Refused("season.create", new JsonObject { ["house"] = house, ["name"] = "Ohne Datum", ["from"] = "morgen", ["to"] = "2032-01-31" }, "from");
        await Refused("season.update", new JsonObject { ["season"] = app.Ids["high"], ["rates"] = new JsonArray(new JsonObject { ["unit"] = "gibt-es-nicht", ["price_cents"] = 1 }) }, "Einheit");
        await Refused("fee.create", new JsonObject { ["house"] = house, ["name"] = "Woche", ["amount_cents"] = 100, ["per"] = "week" }, "per");
        await Refused("price.check", new JsonObject { ["unit"] = app.Ids["a1"], ["arrival"] = "2030-05-01" }, "departure");
        Assert.Equal(before, await Seasons());
        Assert.DoesNotContain((JsonArray)(await RunAsync("unit.list", new JsonObject()))["data"]!, u => u!["name"]!.GetValue<string>() is "Böse" or "Billig" or "Halb");
    }

    [Fact]
    public async Task Internal_undo_helpers_are_refused_to_an_outside_caller()
    {
        foreach (var name in Internals)
            await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(name, new JsonObject { ["id"] = 1 }));
        Assert.Equal("failed", Status(await RunAsync("stay.move", new JsonObject { ["stay"] = 999999, ["to"] = "confirmed" })));
    }

    [Fact]
    public async Task A_request_is_read_and_moved_along_only_through_the_flow()
    {
        await app.AdminAsync();
        var res = await app.App.Browser().PostAsync("/buchung/anfrage", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["einheit"] = "apartment-meerblick", ["anreise"] = "2030-03-10", ["abreise"] = "2030-03-14", ["erwachsene"] = "2", ["kinder"] = "0",
            ["name"] = "Otto Ops", ["email"] = "ops-gast@example.org", ["telefon"] = "+49 555", ["nachricht"] = "Mit Hund.", ["consent"] = "1"
        }));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);

        var open = (JsonArray)Applied(await RunAsync("stay.list", new JsonObject { ["open"] = true, ["search"] = "ops-gast@" }))["data"]!;
        var stay = (JsonObject)open.Single()!;
        Assert.Equal("request", stay["status"]!.GetValue<string>());
        var id = stay["id"]!.GetValue<long>();
        var total = stay["total_cents"]!.GetValue<long>();
        Assert.Equal("failed", Status(await RunAsync("stay.list", new JsonObject { ["status"] = "kaputt" })));

        var full = Applied(await RunAsync("stay.read", new JsonObject { ["stay"] = id }))["data"]!;
        Assert.Equal("Mit Hund.", full["message"]!.GetValue<string>());
        Assert.Equal("+49 555", full["phone"]!.GetValue<string>());
        Assert.NotEmpty(full["lines"]!.AsArray());
        Assert.Null(full["travellers"]);
        var number = full["number"]!.GetValue<string>();

        Assert.False((await RunAsync("price.check", new JsonObject { ["unit"] = app.Ids["a1"], ["arrival"] = "2030-03-11", ["departure"] = "2030-03-13" }))["data"]!["available"]!.GetValue<bool>());
        Assert.Equal("failed", Status(await RunAsync("stay.move", new JsonObject { ["stay"] = id, ["to"] = "expired" })));    // only the job runs a request out

        var offer = Applied(await RunAsync("stay.move", new JsonObject { ["number"] = number, ["to"] = "offer", ["adjust_cents"] = -1000, ["adjust_text"] = "Frühbucher" }));
        Assert.Equal("offer", offer["data"]!["status"]!.GetValue<string>());
        Assert.Equal(total - 1000, offer["data"]!["total_cents"]!.GetValue<long>());
        Assert.Null(offer["undo"]);
        Applied(await RunAsync("stay.move", new JsonObject { ["stay"] = id, ["to"] = "confirmed" }));
        var refused = await RunAsync("stay.move", new JsonObject { ["stay"] = id, ["to"] = "offer" });          // a booking is not offered again
        Assert.Equal("failed", Status(refused));
        var history = string.Join(" | ", (await RunAsync("stay.read", new JsonObject { ["stay"] = id }))["data"]!["history"]!.AsArray().Select(h => h!["text"]!.GetValue<string>()));
        Assert.Contains("Vom Gastgeber bestätigt (über eine Aktion)", history);
        Assert.DoesNotContain("Bestätigung an den Gast", history);                                   // no notify, no mail

        Applied(await RunAsync("stay.move", new JsonObject { ["stay"] = id, ["to"] = "cancelled", ["reason"] = "Krank" }));
        Assert.True((await RunAsync("price.check", new JsonObject { ["unit"] = app.Ids["a1"], ["arrival"] = "2030-03-11", ["departure"] = "2030-03-13" }))["data"]!["available"]!.GetValue<bool>());
        Assert.Equal("cancelled", (await RunAsync("stay.read", new JsonObject { ["stay"] = id }))["data"]!["status"]!.GetValue<string>());
    }
}
