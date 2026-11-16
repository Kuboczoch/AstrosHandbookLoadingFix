using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.GameContent;

namespace AstrosHandbookLoadingFix;

// Constructors are NOT a completion boundary. Only stream pages accepted by the
// known vanilla producer, after recipe caches and object initializers are ready.
[HarmonyPatch(typeof(ModSystemSurvivalHandbook), "onCreatePagesAsync")]
public static class HandbookPageMadePatch
{
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var original = AccessTools.Method(typeof(List<GuiHandbookPage>), nameof(List<GuiHandbookPage>.Add));
        var replacement = AccessTools.Method(typeof(HandbookPageLoader), nameof(HandbookPageLoader.AddAcceptedPage));
        int replacements = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(original))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = replacement;
                replacements++;
            }
            yield return instruction;
        }
        if (replacements != 3) throw new InvalidOperationException($"Expected three survival handbook page-add sites; found {replacements}.");
    }
}
