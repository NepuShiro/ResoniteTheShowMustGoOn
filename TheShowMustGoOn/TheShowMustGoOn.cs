using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text;
using Elements.Core;
using Elements.Assets;
using FrooxEngine;
using FrooxEngine.ProtoFlux;
using FrooxEngine.UIX;
using HarmonyLib;
using ResoniteModLoader;

namespace TheShowMustGoOn
{
	public class TheShowMustGoOn : ResoniteMod
	{
		public static ModConfiguration Config;

		private static readonly ConditionalWeakTable<Component, object> AudioStreams = new();

		[AutoRegisterConfigKey]
		private static readonly ModConfigurationKey<bool> EnableStreamingWhileUnfocused = new("EnableStreamingWhileUnfocused", "Keep streaming audio to unfocused worlds.", () => true);

		[AutoRegisterConfigKey]
		private static readonly ModConfigurationKey<bool> EnableVoiceWhileUnfocused = new("EnableVoiceWhileUnfocused", "Keep streaming voice to unfocused worlds.", () => false);

		public override string Author => "Banane9, NepuShiro, Art0007i";
		public override string Link => "https://github.com/NepuShiro/ResoniteTheShowMustGoOn";
		public override string Name => "TheShowMustGoOn";
		public override string Version => "2.0.0";

		public override void OnEngineInit()
		{
			Harmony harmony = new($"{Author}.{Name}");
			Config = GetConfiguration();
			Config.Save(true);
			harmony.PatchAll();
		}

		[HarmonyPatch]
		private static class AudioStreamSpawnerPatch
		{
			public static MethodInfo FindAsyncBody (MethodInfo mi)
			{
				AsyncStateMachineAttribute asyncAttribute = (AsyncStateMachineAttribute)mi.GetCustomAttribute(typeof(AsyncStateMachineAttribute));
				Type asyncStateMachineType = asyncAttribute.StateMachineType;
				
				return AccessTools.Method(asyncStateMachineType, nameof(IAsyncStateMachine.MoveNext));
			}
			
			// Specify the class and method to be patched
			[HarmonyTargetMethod]
			private static MethodBase TargetMethod()
			{
				// Identify the compiler-generated class and method
				var generatedClassType = typeof(AudioStreamSpawner).GetNestedTypes(BindingFlags.NonPublic)
					.FirstOrDefault(t => t.IsDefined(typeof(CompilerGeneratedAttribute), false) && t.Name.Contains("DisplayClass9_0"));

				return FindAsyncBody(generatedClassType?.GetMethod("<OnStartStreaming>b__0", BindingFlags.NonPublic | BindingFlags.Instance));
			}

			// Define the transpiler
			[HarmonyTranspiler]
			private static IEnumerable<CodeInstruction> BuildUITranspiler(IEnumerable<CodeInstruction> codeInstructions)
			{
				var attachComponentMethod = typeof(ContainerWorker<Component>).GetMethods(AccessTools.all)
					.Single(method => method.IsGenericMethodDefinition && method.Name == nameof(ContainerWorker<Component>.AttachComponent))
					.MakeGenericMethod(typeof(UserAudioStream<StereoSample>));

				foreach (var instruction in codeInstructions)
				{
					// Debug(instruction);
					if (instruction.Calls(attachComponentMethod))
					{
						// Debug("Found Function");
						instruction.opcode = OpCodes.Call;
						instruction.operand = typeof(AudioStreamSpawnerPatch).GetMethod(nameof(MakeAudioStream), AccessTools.all);
					}

					yield return instruction;
				}
			}

			// Helper method to be called instead of the original
			private static UserAudioStream<StereoSample> MakeAudioStream(Slot slot, bool runOnAttachBehavior, Action<UserAudioStream<StereoSample>> beforeAttach)
			{
				var audioStream = slot.AttachComponent(runOnAttachBehavior, beforeAttach);
				AudioStreams.Add(audioStream, null);

				return audioStream;
			}
		}

		[HarmonyPatch]
		private static class UserAudioStreamPatch
		{
			private static bool MuteCheck(Component audioStream)
			{
				var world = audioStream.World;

				return world.Focus == World.WorldFocus.Focused
					|| ((Config.GetValue(EnableVoiceWhileUnfocused)
							|| (Config.GetValue(EnableStreamingWhileUnfocused) && AudioStreams.TryGetValue(audioStream, out _)))
						&& world.Focus == World.WorldFocus.Background);
			}

			private static IEnumerable<MethodBase> TargetMethods()
			{
				var genericOptions = new[]
				{
					typeof(MonoSample),
					typeof(StereoSample),
					typeof(QuadSample),
					typeof(Surround51Sample)
				};

				return genericOptions
					.Select(type => typeof(UserAudioStream<>)
						.MakeGenericType(type)
						.GetMethod(nameof(UserAudioStream<MonoSample>.OnNewAudioData), AccessTools.all));
			}

			private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codeInstructions)
			{
				var getWorldFocusMethod = typeof(World).GetProperty(nameof(World.Focus), AccessTools.all).GetMethod;
				var checkMethod = typeof(UserAudioStreamPatch).GetMethod(nameof(MuteCheck), AccessTools.all);

				var instructions = codeInstructions.ToList();
				var getWorldFocusIndex = instructions.FindIndex(instruction => instruction.Calls(getWorldFocusMethod));

				instructions[getWorldFocusIndex - 1] = new CodeInstruction(OpCodes.Ldarg_0);
				instructions[getWorldFocusIndex] = new CodeInstruction(OpCodes.Call, checkMethod);

				instructions.RemoveAt(getWorldFocusIndex + 1);
				instructions[getWorldFocusIndex + 1].opcode = OpCodes.Brfalse_S;

				return instructions;
			}
		}
	}
}