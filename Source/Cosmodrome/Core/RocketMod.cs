using System;
using System.IO;
using System.Reflection;
using RimWorld;
using UnityEngine;
using Verse;

namespace MissileGirl
{
    public partial class RocketMod : Mod
    {
        public static RocketSettings Settings;

        public static RocketMod Instance;

        public static Vector2 scrollPositionStatSettings = Vector2.zero;

        public RocketMod(ModContentPack content) : base(content)
        {
            LongEventHandler.QueueLongEvent(() =>
            {
                Main.DefsLoaded();
            }, "MissileGirl.MissileGirl", doAsynchronously: false, exceptionHandler: null, showExtraUIInfo: true);

            Finder.Mod = Instance = this;
            Finder.ModContentPack = content;
            if (!Directory.Exists(RocketEnvironmentInfo.CustomConfigFolderPath))
            {
                Directory.CreateDirectory(RocketEnvironmentInfo.CustomConfigFolderPath);
                //MissileGirl.Logger.Message($"MissileGirl: Created MissileGirl config folder at <color=orange>{RocketEnvironmentInfo.CustomConfigFolderPath}</color>");
            }
            Logger.Initialize();
            // Patch all core functions
            RocketStartupPatcher.PatchAll();
            // Program start here
            Finder.PluginsLoader = new RocketPluginsLoader();
            try
            {
                foreach (Assembly assembly in Finder.PluginsLoader.LoadAll())
                {
                    RocketAssembliesInfo.Assemblies.Add(assembly);
                    if (!content.assemblies.loadedAssemblies.Any(a => a.GetName().Name == assembly.GetName().Name))
                    {
                        content.assemblies.loadedAssemblies.Add(assembly);
                    }
                    if (Prefs.LogVerbose)
                    {
                        Logger.Message($"<color=orange>MissileGirl</color>: Loaded <color=red>{assembly.FullName}</color>");
                    }
                }
            }
            catch (Exception er)
            {
                Log.Error($"MissileGirl: loading plugin failed {er.Message}:{er.StackTrace}");
                Logger.Debug("Loading plugins failed", exception: er);
            }
            finally
            {
                RocketAssembliesInfo.Assemblies.AddRange(RocketAssembliesInfo.MissileGirlAssembliesInAppDomain);
                foreach (Assembly assembly in RocketAssembliesInfo.Assemblies)
                {
                    Logger.Debug($"Found in AppDomain after loading assembly {assembly.FullName}", file: "Assemblies.log");
                }
                Main.ReloadActions();
                foreach (var action in Main.onInitialization)
                    action.Invoke();
            }
        }

        public override string SettingsCategory()
        {
            return "MissileGirl";
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            base.DoSettingsWindowContents(inRect);
            DoSettings(inRect);
            WriteSettings();
            GUIUtility.ClearGUIState();
        }

        private static readonly Listing_Collapsible.Group_Collapsible group = new Listing_Collapsible.Group_Collapsible();

        private static readonly Listing_Collapsible collapsible_general = new Listing_Collapsible();

        private static readonly Listing_Collapsible collapsible_junk = new Listing_Collapsible(group);

        private static readonly Listing_Collapsible collapsible_speed = new Listing_Collapsible(group);

        private static readonly Listing_Collapsible collapsible_genMap = new Listing_Collapsible(group);

        private static readonly Listing_Collapsible collapsible_other = new Listing_Collapsible(group);

        private static readonly Listing_Collapsible collapsible_GlowGrid = new Listing_Collapsible(group);

        private static readonly Listing_Collapsible collapsible_debug = new Listing_Collapsible(group);

        private static readonly Listing_Collapsible collapsible_experimental = new Listing_Collapsible(group);

        private static bool guiGroupCreated = false;

        public static void DoSettings(Rect inRect, bool doStats = true, Action<Listing_Standard> extras = null)
        {
            if (!guiGroupCreated)
            {
                guiGroupCreated = true;

                collapsible_junk.Group = group;
                group.Register(collapsible_junk);

                collapsible_other.Group = group;
                group.Register(collapsible_other);

                collapsible_debug.Group = group;
                group.Register(collapsible_debug);

                collapsible_GlowGrid.Group = group;
                group.Register(collapsible_GlowGrid);

                collapsible_experimental.Group = group;
                group.Register(collapsible_experimental);                
            }
            GUIUtility.ExecuteSafeGUIAction(() =>
            {
                collapsible_general.Expanded = true;
                collapsible_general.Begin(inRect, KeyedResources.MissileGirl_Settings, drawIcon: false, drawInfo: false);

                if (collapsible_general.CheckboxLabeled(KeyedResources.MissileGirl_Enable, ref RocketPrefs.Enabled))
                {
                    ResetRocketDebugPrefs();
                }
                if (collapsible_general.CheckboxLabeled("MissileGirl.ShowIcon".Translate(), ref RocketPrefs.MainButtonToggle, "MissileGirl.ShowIcon.Description".Translate()))
                {
                    MainButtonDef mainButton_WindowDef = DefDatabase<MainButtonDef>.GetNamed("RocketWindow", errorOnFail: false);
                    if (mainButton_WindowDef != null)
                    {
                        mainButton_WindowDef.buttonVisible = RocketPrefs.MainButtonToggle;
                        string state = RocketPrefs.MainButtonToggle ? "shown" : "hidden";
                        MissileGirl.Logger.Message($"MissileGirl: <color=red>MainButton</color> is now {state}!");
                    }
                }
                collapsible_general.CheckboxLabeled("MissileGirl.ProgressBar".Translate(), ref RocketPrefs.ShowWarmUpPopup, "MissileGirl.ProgressBar.Description".Translate());
                collapsible_general.End(ref inRect);
                inRect.yMin += 5;

                if (Find.World != null)
                {
                    WorldInfoComponent infoComponent = Find.World.GetComponent<WorldInfoComponent>();
                    collapsible_genMap.Begin(inRect, KeyedResources.MissileGirl_GenMapSize);
                    collapsible_genMap.Label(KeyedResources.MissileGirl_GenMapSize_Text);
                    collapsible_genMap.Line(1);
                    collapsible_genMap.Label(KeyedResources.MissileGirl_GenMapSize_Note);
                    collapsible_genMap.Columns(18, new Action<Rect>[]{
                        (rect)=>{
                            GUIFont.Anchor = TextAnchor.MiddleLeft;
                            float a = infoComponent.InitialMapWidth;
                            string buffer = $"{a}";
                            Widgets.Label(rect, KeyedResources.MissileGirl_GenMapSize_Width);
                            Widgets.TextFieldNumeric(rect.RightHalf(), ref a, ref buffer, 0, 1000);
                            if(infoComponent.InitialMapWidth != a)
                            {
                                infoComponent.InitialMapWidth = (int)a;
                                infoComponent.useCustomMapSizes = true;
                            }
                        },
                        (rect)=>{
                            GUIFont.Anchor = TextAnchor.MiddleLeft;
                            float a = infoComponent.InitialMapHeight;
                            string buffer = $"{a}";
                            Widgets.Label(rect.MoveTopLeftCorner(25f, 0), KeyedResources.MissileGirl_GenMapSize_Height);
                            Widgets.TextFieldNumeric(rect.RightHalf(), ref a, ref buffer, 0, 1000);
                            if(infoComponent.InitialMapHeight != a)
                            {
                                infoComponent.InitialMapHeight = (int)a;
                                infoComponent.useCustomMapSizes = true;
                            }
                        }
                    }, useMargins: true);
                    collapsible_genMap.End(ref inRect);
                    inRect.yMin += 5;
                }

                if (RocketPrefs.Enabled)
                {
                    if (RocketEnvironmentInfo.IsDevEnv)
                    {
                        collapsible_junk.Begin(inRect, "MissileGirl.Junk".Translate());
                        collapsible_junk.CheckboxLabeled("MissileGirl.CorpseRemoval".Translate(), ref RocketPrefs.CorpsesRemovalEnabled, "MissileGirl.CorpseRemoval.Description".Translate());
                        collapsible_junk.End(ref inRect);
                        inRect.yMin += 5;
                    }

                    collapsible_other.Begin(inRect, "MissileGirl.StatCacheSettings".Translate());
                    

                    collapsible_other.CheckboxLabeled("MissileGirl.Adaptive".Translate(), ref RocketPrefs.Learning, "MissileGirl.Adaptive.Description".Translate());
                    collapsible_other.CheckboxLabeled("MissileGirl.AdaptiveAlert.Label".Translate(), ref RocketPrefs.LearningAlertEnabled, "MissileGirl.AdaptiveAlert.Description".Translate());                    
                    collapsible_other.CheckboxLabeled("MissileGirl.EnableGearStatCaching".Translate(), ref RocketPrefs.StatGearCachingEnabled);
                    collapsible_other.Line(1);
                    collapsible_other.CheckboxLabeled(KeyedResources.MissileGirl_FixBeauty, ref RocketPrefs.FixBeauty, KeyedResources.MissileGirl_FixBeauty_Tip);
                    collapsible_other.End(ref inRect);
                    inRect.yMin += 5;

                    if (Prefs.DevMode || RocketEnvironmentInfo.IsDevEnv)
                    {
                        collapsible_experimental.Begin(inRect, KeyedResources.MissileGirl_Experimental);                        
                        // if (RocketEnvironmentInfo.IsDevEnv)
                        // {
                        //    collapsible_experimental.CheckboxLabeled(KeyedResources.MissileGirl_TranslationCaching, ref RocketPrefs.TranslationCaching);
                        //    collapsible_experimental.Line(1);
                        // }
                        // collapsible_experimental.Label(KeyedResources.MissileGirl_Experimental_Description);
                        bool devKeyEnabled = File.Exists(RocketEnvironmentInfo.DevKeyFilePath);
                        if (collapsible_experimental.CheckboxLabeled(KeyedResources.MissileGirl_Experimental_OptInBeta, ref devKeyEnabled))
                        {
                            if (!devKeyEnabled && File.Exists(RocketEnvironmentInfo.DevKeyFilePath))
                            {
                                File.Delete(RocketEnvironmentInfo.DevKeyFilePath);
                                RocketPrefs.TimeDilationColonists = false;
                            }
                            if (devKeyEnabled && !File.Exists(RocketEnvironmentInfo.DevKeyFilePath))
                                File.WriteAllText(RocketEnvironmentInfo.DevKeyFilePath, "enabled");
                        }
                        //collapsible_experimental.Line(1);
                        //collapsible_experimental.CheckboxLabeled(KeyedResources.MissileGirl_FixBeauty, ref RocketPrefs.FixBeauty, KeyedResources.MissileGirl_FixBeauty_Tip);
                        collapsible_experimental.End(ref inRect);
                        inRect.yMin += 5;
                    }
                    collapsible_debug.Begin(inRect, "Debugging options");

                    if (collapsible_debug.CheckboxLabeled("MissileGirl.Debugging".Translate(), ref RocketDebugPrefs.Debug, "MissileGirl.Debugging.Description".Translate())
                    && !RocketDebugPrefs.Debug)
                    {
                        ResetRocketDebugPrefs();
                    }
                    if (RocketDebugPrefs.Debug)
                    {
                        collapsible_debug.Line(1);
                        collapsible_debug.CheckboxLabeled("Enable Stat Logging (Will kill performance)", ref RocketDebugPrefs.StatLogging);
                        collapsible_debug.Gap();
                    }
                    collapsible_debug.End(ref inRect);
                }
            });
        }

        public static void ResetRocketDebugPrefs()
        {
            RocketDebugPrefs.Debug = false;
            RocketDebugPrefs.Debug150MTPS = false;
            RocketDebugPrefs.LogData = false;
            RocketDebugPrefs.StatLogging = false;
            RocketDebugPrefs.FlashDilatedPawns = false;
            RocketDebugPrefs.AlwaysDilating = false;
            RocketPrefs.EnableGridRefresh = false;
            RocketPrefs.RefreshGrid = false;
            RocketStates.SingleTickIncrement = false;
        }
    }
}