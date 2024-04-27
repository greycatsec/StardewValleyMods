﻿using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.Menus;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace SocialPageOrderMenu
{
    public class ModEntry : Mod
    {
        public static ModConfig Config;
        public static IMonitor SMonitor;
        public static IModHelper SHelper;
        public static int xOffset = 16;

        public static readonly PerScreen<MyOptionsDropDown> dropDown = new();
        public static readonly PerScreen<bool> wasSorted = new PerScreen<bool>(() => false);
        public static readonly PerScreen<int> currentSort = new PerScreen<int>(() => 0);

        private static readonly PerScreen<string> lastFilterString = new PerScreen<string>(()=>"");
        private static readonly PerScreen<TextBox> filterField = new();
        private static readonly PerScreen<List<SocialPage.SocialEntry>> allEntries = new PerScreen<List<SocialPage.SocialEntry>>(() => new List<SocialPage.SocialEntry>());


        public override void Entry(IModHelper helper)
        {
            Config = Helper.ReadConfig<ModConfig>();
            if (!Config.EnableMod)
                return;
            SMonitor = Monitor;
            SHelper = Helper;

            helper.Events.Input.ButtonPressed += Input_ButtonPressed;
            helper.Events.GameLoop.GameLaunched += GameLoop_GameLaunched;

            var harmony = new Harmony(ModManifest.UniqueID);
            harmony.PatchAll();

            harmony.Patch(AccessTools.Constructor(typeof(SocialPage), new Type[] { typeof(int), typeof(int), typeof(int), typeof(int) }),
                postfix: new HarmonyMethod(typeof(ModEntry), nameof(SocialPage_Constructor_Postfix))
            );

            harmony.Patch(AccessTools.Method(typeof(IClickableMenu), nameof(IClickableMenu.receiveKeyPress)),
                prefix: new HarmonyMethod(typeof(ModEntry), nameof(SocialPage_recieveKeyPress_Prefix))
            );
            
            harmony.Patch(AccessTools.Method(typeof(SocialPage), nameof(SocialPage.performHoverAction)),
                postfix: new HarmonyMethod(typeof(ModEntry), nameof(SocialPage_performHoverAction_Postfix))
            );
            
            harmony.Patch(AccessTools.Method(typeof(SocialPage), nameof(SocialPage.updateSlots)),
                prefix: new HarmonyMethod(typeof(ModEntry), nameof(SocialPage_updateSlots_Prefix))
            );
            
            harmony.Patch(AccessTools.Method(typeof(GameMenu), nameof(GameMenu.changeTab)),
                postfix: new HarmonyMethod(typeof(ModEntry), nameof(GameMenu_changeTab_Postfix))
            );
        }

        private void GameLoop_GameLaunched(object sender, GameLaunchedEventArgs e)
        {

            // get Generic Mod Config Menu's API (if it's installed)
            var configMenu = Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
            if (configMenu is null)
                return;

            // register mod
            configMenu.Register(
                mod: ModManifest,
                reset: () => Config = new ModConfig(),
                save: () => Helper.WriteConfig(Config)
            );

            configMenu.AddBoolOption(
                mod: ModManifest,
                name: () => "Mod Enabled?",
                getValue: () => Config.EnableMod,
                setValue: value => Config.EnableMod = value
            );

            configMenu.AddKeybind(
                mod: ModManifest,
                name: () => "Prev Sort Key",
                getValue: () => Config.prevButton,
                setValue: value => Config.prevButton = value
            );

            configMenu.AddKeybind(
                mod: ModManifest,
                name: () => "Next Sort Key",
                getValue: () => Config.nextButton,
                setValue: value => Config.nextButton = value
            );

            configMenu.AddBoolOption(
                mod: ModManifest,
                name: () => "Use Filter",
                getValue: () => Config.UseFilter,
                setValue: (value) => SetUseFilter(value));

            configMenu.AddNumberOption(
                mod: ModManifest,
                name: () => "Dropdown X Offset",
                getValue: () => Config.DropdownOffsetX,
                setValue: value => Config.DropdownOffsetX = value
            );
            
            configMenu.AddNumberOption(
                mod: ModManifest,
                name: () => "Dropdown Y Offset",
                getValue: () => Config.DropdownOffsetY,
                setValue: value => Config.DropdownOffsetY = value
            );
            
            configMenu.AddNumberOption(
                mod: ModManifest,
                name: () => "Filter X Offset",
                getValue: () => Config.FilterOffsetX,
                setValue: value => Config.FilterOffsetX = value
            );
            
            configMenu.AddNumberOption(
                mod: ModManifest,
                name: () => "Filter Y Offset",
                getValue: () => Config.FilterOffsetY,
                setValue: value => Config.FilterOffsetY = value
            );
        }

        private void Input_ButtonPressed(object sender, ButtonPressedEventArgs e)
        {
            if (!Config.EnableMod || Game1.activeClickableMenu is not GameMenu || (Game1.activeClickableMenu as GameMenu).GetCurrentPage() is not SocialPage)
                return;
            if (e.Button == Config.prevButton)
            {
                int sort = currentSort.Value;
                sort--;
                if (sort < 0)
                    sort = 3;
                currentSort.Value = sort;
            }
            else if (e.Button == Config.nextButton)
            {
                int sort = currentSort.Value;
                sort++;
                sort %= 4;
                currentSort.Value = sort;
            }
            else
                return;
            dropDown.Value.selectedOption = currentSort.Value;
            Helper.WriteConfig(Config);
            ResortSocialList();
        }

        public static void SocialPage_Constructor_Postfix(SocialPage __instance)
        {
            if (!Config.EnableMod)
                return;
            dropDown.Value = new MyOptionsDropDown("", 0);

            if(Config.UseFilter)
            {
                filterField.Value = new TextBox(Game1.content.Load<Texture2D>("LooseSprites\\textBox"), null, Game1.smallFont, Game1.textColor)
                {
                    Text = ""
                };
            }

            for (int i = 0; i < 4; i++)
            {
                dropDown.Value.dropDownDisplayOptions.Add(SHelper.Translation.Get($"sort-{i}"));
                dropDown.Value.dropDownOptions.Add(SHelper.Translation.Get($"sort-{i}"));
            }
            dropDown.Value.RecalculateBounds();
            dropDown.Value.selectedOption = currentSort.Value;
            wasSorted.Value = false;

            allEntries.Value.Clear();
            allEntries.Value.AddRange(__instance.SocialEntries);
        }

        [HarmonyPatch(typeof(SocialPage), nameof(SocialPage.draw), new Type[] { typeof(SpriteBatch) })]
        public class SocialPage_drawTextureBox_Patch
        {
            public static void Prefix(SocialPage __instance, SpriteBatch b)
            {
                if (!Config.EnableMod)
                    return;
                if (!wasSorted.Value)
                {
                    wasSorted.Value = true;
                    ResortSocialList();
                }
            }
            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                SMonitor.Log("Transpiling SocialPage.draw");
                var codes = new List<CodeInstruction>(instructions);
                int index = codes.FindLastIndex(ci => ci.opcode == OpCodes.Call && (MethodInfo)ci.operand == AccessTools.Method(typeof(IClickableMenu), nameof(IClickableMenu.drawTextureBox), new Type[] { typeof(SpriteBatch), typeof(Texture2D), typeof(Rectangle), typeof(int), typeof(int), typeof(int), typeof(int), typeof(Color), typeof(float), typeof(bool), typeof(float) }));
                if(index > -1)
                {
                    SMonitor.Log("Inserting dropdown draw method");
                    codes.Insert(index + 1, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SocialPage_drawTextureBox_Patch), nameof(DrawDropDown))));
                    codes.Insert(index + 1, new CodeInstruction(OpCodes.Ldarg_1));
                    codes.Insert(index + 1, new CodeInstruction(OpCodes.Ldarg_0));

                }
                return codes.AsEnumerable();
            }
            public static void DrawDropDown(SocialPage page, SpriteBatch b)
            {
                if (!Config.EnableMod)
                    return;
                dropDown.Value.draw(b, GetDropdownX(page), GetDropdownY(page));
                if (SHelper.Input.IsDown(SButton.MouseLeft) && AccessTools.FieldRefAccess<OptionsDropDown, bool>(dropDown.Value, "clicked") && dropDown.Value.dropDownBounds.Contains(Game1.getMouseX() - GetDropdownX(page), Game1.getMouseY() - GetDropdownY(page)))
                {
                    dropDown.Value.selectedOption = (int)Math.Max(Math.Min((float)(Game1.getMouseY() - GetDropdownY(page) - dropDown.Value.dropDownBounds.Y) / (float)dropDown.Value.bounds.Height, (float)(dropDown.Value.dropDownOptions.Count - 1)), 0f);
                }

                if(Config.UseFilter)
                {
                    UpdateFilterPosition(page);
                    filterField.Value.Draw(b);
                }
            }
        }
        [HarmonyPatch(typeof(SocialPage), nameof(SocialPage.receiveLeftClick))]
        public class SocialPage_receiveLeftClick_Patch
        {
            public static bool Prefix(SocialPage __instance, int x, int y)
            {
                if (!Config.EnableMod)
                    return true;
                if (dropDown.Value.bounds.Contains(x - GetDropdownX(__instance), y - GetDropdownY(__instance)))
                {
                    dropDown.Value.receiveLeftClick(x - GetDropdownX(__instance), y - GetDropdownY(__instance));
                    return false;
                }

                if(Config.UseFilter)
                    filterField.Value.Update();
                return true;
            }
        }
        [HarmonyPatch(typeof(SocialPage), nameof(SocialPage.releaseLeftClick))]
        public class SocialPage_releaseLeftClick_Patch
        {
            public static bool Prefix(SocialPage __instance, int x, int y)
            {
                if (!Config.EnableMod)
                    return true;
                if (AccessTools.FieldRefAccess<OptionsDropDown, bool>(dropDown.Value, "clicked"))
                {
                    if(dropDown.Value.dropDownBounds.Contains(Game1.getMouseX() - GetDropdownX(__instance), Game1.getMouseY() - GetDropdownY(__instance)))
                    {
                        dropDown.Value.leftClickReleased(Game1.getMouseX() - GetDropdownX(__instance), Game1.getMouseY() - GetDropdownY(__instance));
                    }
                    return false;
                }
                return true;
            }
        }

        public static bool SocialPage_recieveKeyPress_Prefix(IClickableMenu __instance, Keys key)
        {
            if (!Config.EnableMod || !Config.UseFilter || filterField.Value is null || !filterField.Value.Selected || key == Keys.Escape || __instance is not SocialPage socialPage)
                return true;

            socialPage.updateSlots();
            return false;
        }

        public static void SocialPage_performHoverAction_Postfix(int x, int y)
        {
            if (!Config.EnableMod || !Config.UseFilter)
                return;
            filterField.Value.Hover(x, y);
        }

        public static void SocialPage_updateSlots_Prefix(SocialPage __instance)
        {
            if (!Config.EnableMod || !Config.UseFilter || filterField.Value is null || lastFilterString.Value == filterField.Value.Text)
                return;

            lastFilterString.Value = filterField.Value.Text;

            __instance.SocialEntries.Clear();

            __instance.SocialEntries.AddRange(filterField.Value.Text == "" ? allEntries.Value : allEntries.Value.Where((entry)=>entry.DisplayName.ToLower().StartsWith(filterField.Value.Text)));

            __instance.CreateComponents();

            ResortSocialList();
        }

        public static void GameMenu_changeTab_Postfix(GameMenu __instance)
        {
            if (!Config.EnableMod || !Config.UseFilter || filterField.Value is null)
                return;

            if(__instance.currentTab != GameMenu.socialTab)
            {
                filterField.Value.Selected = false;
            }
            else
            {
                filterField.Value.Selected = true;
            }
        }

        public static void ResortSocialList()
        {
            if (Game1.activeClickableMenu is GameMenu)
            {
                SocialPage page = (Game1.activeClickableMenu as GameMenu).pages[GameMenu.socialTab] as SocialPage;

                List<NameSpriteSlot> nameSprites = new List<NameSpriteSlot>();
                List<ClickableTextureComponent> sprites = new List<ClickableTextureComponent>(SHelper.Reflection.GetField<List<ClickableTextureComponent>>(page, "sprites").GetValue());
                for (int i = 0; i < page.SocialEntries.Count; i++)
                {
                    nameSprites.Add(new NameSpriteSlot(page.SocialEntries[i], sprites[i], page.characterSlots[i]));
                }
                switch (currentSort.Value)
                {
                    case 0: // friend asc
                        SMonitor.Log("sorting by friend asc");
                        nameSprites.Sort(delegate (NameSpriteSlot x, NameSpriteSlot y)
                        {
                            bool xIsPlayerOrNullFriendship = x.entry.IsPlayer || x.entry.Friendship is null || x.entry.IsChild;
                            bool yIsPlayerOrNullFriendship = y.entry.IsPlayer || y.entry.Friendship is null || y.entry.IsChild;
                            if (xIsPlayerOrNullFriendship && yIsPlayerOrNullFriendship)
                                return 0;

                            if (xIsPlayerOrNullFriendship)
                                return 1;

                            if (yIsPlayerOrNullFriendship)
                                return -1;

                            int c = x.entry.Friendship.Points.CompareTo(y.entry.Friendship.Points);
                            if (c == 0)
                                c = x.entry.DisplayName.CompareTo(y.entry.DisplayName);
                            return c;

                        });
                        break;
                    case 1: // friend desc
                        SMonitor.Log("sorting by friend desc");
                        nameSprites.Sort(delegate (NameSpriteSlot x, NameSpriteSlot y)
                        {
                            bool xIsPlayerOrNullFriendship = x.entry.IsPlayer || x.entry.Friendship is null || x.entry.IsChild;
                            bool yIsPlayerOrNullFriendship = y.entry.IsPlayer || y.entry.Friendship is null || y.entry.IsChild;
                            if (xIsPlayerOrNullFriendship && yIsPlayerOrNullFriendship)
                                return 0;

                            if (xIsPlayerOrNullFriendship)
                                return 1;

                            if (yIsPlayerOrNullFriendship)
                                return -1;

                            int c = -x.entry.Friendship.Points.CompareTo(y.entry.Friendship.Points);
                            if (c == 0)
                                c = x.entry.DisplayName.CompareTo(y.entry.DisplayName);
                            return c;

                        });
                        break;
                    case 2: // alpha asc
                        SMonitor.Log("sorting by alpha asc");
                        nameSprites.Sort(delegate (NameSpriteSlot x, NameSpriteSlot y)
                        {
                            if(!x.entry.IsMet && !y.entry.IsMet)
                                return 0;
                            if (!x.entry.IsMet)
                                return 1;
                            if (!y.entry.IsMet)
                                return -1;

                            return x.entry.DisplayName.CompareTo(y.entry.DisplayName);
                        });
                        break;
                    case 3: // alpha desc
                        SMonitor.Log("sorting by alpha desc");
                        nameSprites.Sort(delegate (NameSpriteSlot x, NameSpriteSlot y)
                        {
                            if (!x.entry.IsMet && !y.entry.IsMet)
                                return 0;
                            if (!x.entry.IsMet)
                                return 1;
                            if (!y.entry.IsMet)
                                return -1;
                            return -x.entry.DisplayName.CompareTo(y.entry.DisplayName);
                        });
                        break;
                }
                var cslots = ((Game1.activeClickableMenu as GameMenu).pages[GameMenu.socialTab] as SocialPage).characterSlots;
                for (int i = 0; i < nameSprites.Count; i++)
                {
                    nameSprites[i].slot.myID = i;
                    nameSprites[i].slot.downNeighborID = i + 1;
                    nameSprites[i].slot.upNeighborID = i - 1;
                    if (nameSprites[i].slot.upNeighborID < 0)
                    {
                        nameSprites[i].slot.upNeighborID = 12342;
                    }
                    sprites[i] = nameSprites[i].sprite;
                    nameSprites[i].slot.bounds = cslots[i].bounds;
                    cslots[i] = nameSprites[i].slot;
                    page.SocialEntries[i] = nameSprites[i].entry;
                }
                SHelper.Reflection.GetField<List<ClickableTextureComponent>>((Game1.activeClickableMenu as GameMenu).pages[GameMenu.socialTab], "sprites").SetValue(new List<ClickableTextureComponent>(sprites));

                int first_character_index = 0;
                for (int l = 0; l < page.SocialEntries.Count; l++)
                {
                    if (!(((SocialPage)(Game1.activeClickableMenu as GameMenu).pages[GameMenu.socialTab]).SocialEntries[l].IsPlayer))
                    {
                        first_character_index = l;
                        break;
                    }
                }
                SHelper.Reflection.GetField<int>((Game1.activeClickableMenu as GameMenu).pages[GameMenu.socialTab], "slotPosition").SetValue(first_character_index);
                SHelper.Reflection.GetMethod((Game1.activeClickableMenu as GameMenu).pages[GameMenu.socialTab], "setScrollBarToCurrentIndex").Invoke();
                ((SocialPage)(Game1.activeClickableMenu as GameMenu).pages[GameMenu.socialTab]).updateSlots();
            }
        }

        public static void UpdateFilterPosition(SocialPage page)
        {
            filterField.Value.X = page.xPositionOnScreen + page.width / 2 - filterField.Value.Width / 2 + Config.FilterOffsetX;
            filterField.Value.Y = page.yPositionOnScreen + page.height + dropDown.Value.bounds.Height + 20 + Config.FilterOffsetY;
        }

        public static int GetDropdownX(SocialPage page)
        {
            return page.xPositionOnScreen + page.width / 2 - dropDown.Value.bounds.Width / 2 + Config.DropdownOffsetX;
        }
        public static int GetDropdownY(SocialPage page)
        {
            return page.yPositionOnScreen + page.height + Config.DropdownOffsetY;
        }

        public static void SetUseFilter(bool value)
        {
            if(value)
            {
                if(filterField.Value is null)
                {
                    filterField.Value = new TextBox(Game1.content.Load<Texture2D>("LooseSprites\\textBox"), null, Game1.smallFont, Game1.textColor)
                    {
                        Text = ""
                    };
                }
            }

            Config.UseFilter = value;
        }
    }



    internal class NameSpriteSlot
    {
        public SocialPage.SocialEntry entry;
        public ClickableTextureComponent sprite;
        public ClickableTextureComponent slot;

        public NameSpriteSlot(SocialPage.SocialEntry obj, ClickableTextureComponent clickableTextureComponent, ClickableTextureComponent slotComponent)
        {
            entry = obj;
            sprite = clickableTextureComponent;
            slot = slotComponent;
        }
    }
}