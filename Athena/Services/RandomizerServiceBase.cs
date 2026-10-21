// SPDX-License-Identifier: GPL-3.0-only
using Athena.Config;
using Athena.Models;
using Athena.Utilities;
using EldenRingParamsEditor;
using System.Diagnostics;
using System.IO;
using UniversalReplacementRandomizer;
using static Org.BouncyCastle.Crypto.Engines.SM2Engine;

namespace Athena.Services;

public enum BaseMode
{
    Default,
    Baseplus
}

public class RandomizerServiceBase
{
    private string SeedManagerPrefix;
    RandomizerServiceStartingClass randomizerServiceStartingClass = new();
    ModeService modeService = new();

    public RandomizerServiceBase(string appVersion)
    {
        SeedManagerPrefix = "base" + appVersion;
    }

    public void RandomizeBase(int? baseSeed,
                              BaseMode mode,
                              Action<int?>? updateBaseSeedCallback,
                              Action<int?>? updateRandomizedSeedCallback,
                              Action<BaseMode?>? updatedRandomizedModeBaseCallback)
    {
        using DebugTimer _ = new DebugTimer("RandomizeBase");

        MenuBndEditorService menuBndEditor;
        ParamsEditor editor;

        string seedManagerPrefix;


        switch (mode)
        {
            case BaseMode.Baseplus:
                {
                    menuBndEditor = MenuBndEditorService.ReadFromMenuBndFilePath(Constants.MenuBndInBaseplus);
                    editor = ParamsEditor.ReadFromRegulationPath(Constants.RegulationInBaseplus);
                    editor.SetGraceEventFlagId(180001);
                    editor.SetGraceEventFlagId(61423700);
                    editor.SetGraceEventFlagId(61423601);
                    seedManagerPrefix = SeedManagerPrefix + "_baseplus";
                    // Initialize the Talisman Shop
                    InitBaseplusShop(editor);
                    break;
                }
            default:
                {
                    menuBndEditor = MenuBndEditorService.ReadFromMenuBndFilePath(Constants.MenuBndInBase);
                    editor = ParamsEditor.ReadFromRegulationPath(Constants.RegulationInBase);
                    seedManagerPrefix = SeedManagerPrefix;
                    break;
                }
        }

        var urr = new OptimizedReplacementRandomizer(SeedManagerPrefix, baseSeed);
        if (baseSeed == null)
        {
            updateBaseSeedCallback?.Invoke(urr.GetBaseSeed());
        }

        // Generate stats and starting equipment
        RandomizeStartingClassesBase(editor, menuBndEditor, urr);

        // Randomize weapons, spells, and incantations
        RandomizeAllGroupsBase(editor, urr, mode);

        // swap the solo tree sentinel's halberd with the tree sentinel duo's greatshield
        SwapTreeSentinelDrops(editor);

        // Override the base remembrances for better square accessibility
        RandomizeRemembrancesWithWeaponsOnly(editor, urr);

        // Randomize the perfume bottles
        RandomizePerfumeBottles(editor, urr);

        // Disable upgrading Serpent-Hunter
        DisableSerpentHunter(editor);

        // Pidia sells magic scorpion charm
        GivePidiaMagicScorpionCharm(editor);

        ModeCustomization(mode);

        editor.WriteToRegulationPath(Constants.RegulationOutBase);
        menuBndEditor.WriteToMenuBndFilePath(Constants.MenuBndOutBase);

        updateRandomizedSeedCallback?.Invoke(urr.GetBaseSeed());
        updatedRandomizedModeBaseCallback?.Invoke(mode);
    }

    private void RandomizeStartingClassesBase(ParamsEditor editor, MenuBndEditorService menuBndEditor, OptimizedReplacementRandomizer urr)
    {
        using DebugTimer _ = new DebugTimer("RandomizeStartingClassesBase");

        var classStatAllocations = randomizerServiceStartingClass.GenerateAllClassStats(urr);
        var armorSetAllocations = randomizerServiceStartingClass.GenerateArmorSets(urr, $"{Constants.Misc}/base/starting_classes");
        var weaponSetAllocations = randomizerServiceStartingClass.GenerateWeaponSets(urr, $"{Constants.Misc}/base/starting_classes");

        for (int i = 0; i < ParamsEditor.TotalStartingClasses; i++)
        {
            int charaInitId = ParamsEditor.VagabondCharaInitId + i;

            // remove all starting gear, spells, etc.
            randomizerServiceStartingClass.ClearLoadout(editor, charaInitId);

            // allocate randomly generated stats
            var statAllocation = classStatAllocations[i];
            randomizerServiceStartingClass.ApplyStatAllocation(editor, charaInitId, statAllocation);
            editor.SetInitialRuneLevel(charaInitId, 9);

            // allocate randomly generated armor
            var armorAllocation = armorSetAllocations[i];
            randomizerServiceStartingClass.ApplyArmorSet(editor, charaInitId, armorAllocation);

            // allocate randomly generated weapons
            var weaponAllocation = weaponSetAllocations[i];
            randomizerServiceStartingClass.ApplyWeaponSet(editor, charaInitId, weaponAllocation);

            // get class description with all weapons/stat deficits (if any)
            string classDescription = randomizerServiceStartingClass.GenerateClassDescription(editor, armorAllocation, weaponAllocation, statAllocation);

            // guaranteed sorcery / staff
            if (i == 4) // astrologer
            {
                List<GameItemModel> sorceries = CsvReaderUtils.Read<GameItemModel>($"{Constants.Misc}/base/starting_classes/sorceries.csv");
                Random rng = urr.GetSeedManager().GetRandomByKey("starting_sorceries");
                GameItemModel sorcery = sorceries[rng.Next(sorceries.Count)];
                editor.SetInitialEquipSpell(charaInitId, 0, sorcery.ID);
                editor.SetInitialEquipWepLeft(charaInitId, 1, Constants.GlintstoneStaffItemId);
                classDescription += $", Glintstone Staff, {sorcery.Name}";
            }

            // guaranteed incantation / seal
            if (i == 5) // prophet
            {
                List<GameItemModel> incantations = CsvReaderUtils.Read<GameItemModel>($"{Constants.Misc}/base/starting_classes/incantations.csv");
                Random rng = urr.GetSeedManager().GetRandomByKey("starting_incantations");
                GameItemModel incantation = incantations[rng.Next(incantations.Count)];
                editor.SetInitialEquipSpell(charaInitId, 0, incantation.ID);
                editor.SetInitialEquipWepLeft(charaInitId, 1, Constants.FingerSealItemId);
                classDescription += $", Finger Seal, {incantation.Name}";
            }

            menuBndEditor.SetClassDescription(i, classDescription);
        }
    }

    public void RandomizeAllGroupsBase(ParamsEditor editor, OptimizedReplacementRandomizer urr, BaseMode mode)
    {
        var weaponIdsToItemLotMap = editor.GetWeaponIdsToItemLotMap();
        var weaponIdsToItemLotEnemy = editor.GetWeaponIdsToItemLotEnemy();
        var weaponIdsToShopLineup = editor.GetWeaponIdsToShopLineup();

        var goodsIdsToItemLotMap = editor.GetGoodsIdsToItemLotMap();
        var goodsIdsToItemLotEnemy = editor.GetGoodsIdsToItemLotEnemy();
        var goodsIdsToShopLineup = editor.GetGoodsIdsToShopLineup();

        string randomizationgrouppath = Constants.RandomizationGroupsBase;
        if (mode == BaseMode.Baseplus)
        {
            randomizationgrouppath = Constants.RandomizationGroupsBaseplus;
        }


        // incantations, dragon communion incantations, sorceries
        ReplacementUtils.Randomize<GameItemModel>(editor,
                                                  urr,
                                                  $"{randomizationgrouppath}/spells",
                                                  goodsIdsToItemLotMap,
                                                  goodsIdsToItemLotEnemy,
                                                  goodsIdsToShopLineup);

        // chance weapons (randomized within classes)
        ReplacementUtils.RandomizeItemLotEnemy<WeaponModel>(editor,
                                                            urr,
                                                            $"{randomizationgrouppath}/chance_weapons",
                                                            weaponIdsToItemLotEnemy);

        // guaranteed and map weapons (randomized within classes)
        ReplacementUtils.Randomize<GameItemModel>(editor,
                                                  urr,
                                                  $"{randomizationgrouppath}/map_guaranteed_weapons",
                                                  weaponIdsToItemLotMap,
                                                  weaponIdsToItemLotEnemy,
                                                  weaponIdsToShopLineup);

        // remembrance weapons
        ReplacementUtils.RandomizeAndReplaceShopLineupFile<WeaponModel>(editor,
                                                                       urr,
                                                                       $"{randomizationgrouppath}/remembrances/weapons.csv",
                                                                       weaponIdsToShopLineup);

        // remembrance sorceries
        ReplacementUtils.RandomizeAndReplaceShopLineupFile<WeaponModel>(editor,
                                                                       urr,
                                                                       $"{randomizationgrouppath}/remembrances/sorceries.csv",
                                                                       goodsIdsToShopLineup);

        // remembrance incantations
        ReplacementUtils.RandomizeAndReplaceShopLineupFile<WeaponModel>(editor,
                                                                       urr,
                                                                       $"{randomizationgrouppath}/remembrances/incantations.csv",
                                                                       goodsIdsToShopLineup);

        // shop weapons
        ReplacementUtils.RandomizeAndReplaceShopLineupDir<WeaponModel>(editor,
                                                                       urr,
                                                                       $"{randomizationgrouppath}/shop_weapons",
                                                                       weaponIdsToShopLineup);
    }

    private void RandomizeRemembrancesWithWeaponsOnly(ParamsEditor editor, OptimizedReplacementRandomizer urr)
    {
        // We put only weapon checks at the following remembrances, because they are the only realistic checks in a game of
        // base game bingo, and we want the remembrance boss with remembrance weapon square to be more accessible.
        List<int> targetRemembrances = new List<int>() {
            101900, 101901, // Godrick
            101902, 101903, // Starscourge Radahn
            101904, 101905, // Morgott
            101906, 101907, // Rykard
            101910, 101911, // Mohg
            101918, 101919, // Rennala
            101924, 101925, // Regal Ancestor Spirit
        };

        List<int> remembranceWeapons = new List<int>() {
            4550000,  // Greatsword of Radahn (Light)
            4530000,  // Greatsword of Radahn (Lord)
            4020000,  // Maliketh's Black Blade
            4050000,  // Starscourge Greatsword
            23050000, // Axe of Godfrey
            23520000, // Gazing Finger
            23510000, // Shadow Sunflower Blossom
            8100000,  // Morgott's Cursed Sword
            21060000, // Grafted Dragon
            13030000, // Bastard's Stars
            17010000, // Mohgwyn's Sacred Spear
            17500000, // Spear of the Impaler
            15040000, // Axe of Godrick
            8500000,  // Putrescence Cleaver
            15110000, // Winged Greathorn
            42000000, // Lion Greatbow
            3140000,  // Blasphemous Blade
            3510000,  // Greatsword of Damnation
            3100000,  // Sacred Relic Sword
            18510000, // Poleblade of the Bud
            11150000, // Marika's Hammer
            6040000,  // Dragon King's Cragblade
            4400039,  // Sword Lance (Spinning Gravity Thrust)
            9020000,  // Hand of Malenia
            67520000, // Rellana's Twin Blades
            20060000, // Giant's Red Braid
        };

        OptimizedRandomizationGroup remembranceWeaponsGroup = new(targetRemembrances.Count, remembranceWeapons.Count);
        urr.AddGroup("override_remembrance_weapons", remembranceWeaponsGroup);
        int[] replacementIndexes = urr.RandomizeGroup("override_remembrance_weapons");

        // Apply replacements
        for (int i = 0; i < targetRemembrances.Count; i++)
        {
            int target = targetRemembrances[i];
            int replacement = remembranceWeapons[replacementIndexes[i]];
            editor.SetShopLineupEquipId(target, replacement);
            // Quick hack for the only weapon with an ash of war: Sword Lance
            editor.SetShopLineupEquipType(target, replacement == 4400039 ? Constants.EquipTypeCustomWeapon : Constants.EquipTypeWeapon);
        }
    }

    private void RandomizePerfumeBottles(ParamsEditor editor, OptimizedReplacementRandomizer urr)
    {
        // Locations to remove perfume bottles (key items)
        List<int> perfumeBottlesToRemove = new List<int>()
        {
            11000130,   // Leyndell chest
            11000470,   // Leyndell path to grand lift
            31180000,   // Perfumer's Grotto
            1036520070, // Perfumer's Ruins (on ledge)
            1039510000, // Altus by omen
            1048380010, // Caelid
        };
        int goldTingedReplacement = 20830;

        // Remove key item perfume bottles from 5 locations (place gold-tinged excrement instead)
        for (int i = 0; i < perfumeBottlesToRemove.Count; i++)
        {
            editor.SetItemLotMapLotItemId(perfumeBottlesToRemove[i], 0, goldTingedReplacement);
            editor.SetItemLotMapCategory(perfumeBottlesToRemove[i], 0, Constants.CategoryGoods);
        }

        // Remove the merchant perfume bottle
        int targetShopLineupPerfumeBottle = 100725;
        editor.SetShopLineupEquipId(targetShopLineupPerfumeBottle, goldTingedReplacement);
        editor.SetShopLineupEquipType(targetShopLineupPerfumeBottle, Constants.EquipTypeGoods);

        // The perfume bottles to randomize
        List<int> perfumeBottleIDs = new List<int>()
        {
            61500000, // Firespark
            61510000, // Chilling Mist
            61520000, // Frenzy Flame
            61540000, // Deadly Poison
        };

        // Locations to exchange perfume bottles (key items) with perfume bottles (weapons)
        List<int> targetItemLotPerfumeBottles = new List<int>()
        {
           1036510020, // Perfumer's Ruins (near Omenkiller)
           31180000,   // Perfumer's Grotto
           16000110,   // Volcano manor
           1039540040, // Shaded Castle
        };

        // Create the randomization indexes
        OptimizedRandomizationGroup perfumeBottlesGroup = new(perfumeBottleIDs.Count, perfumeBottleIDs.Count);
        urr.AddGroup("override_perfume_bottles", perfumeBottlesGroup);
        int[] replacementIndexes = urr.RandomizeGroup("override_perfume_bottles");

        // Place randomly chosen perfume bottles at the four item lot locations
        for (int i = 0; i < targetItemLotPerfumeBottles.Count; i++)
        {
            editor.SetItemLotMapLotItemId(targetItemLotPerfumeBottles[i], 0, perfumeBottleIDs[replacementIndexes[i]]);
            editor.SetItemLotMapCategory(targetItemLotPerfumeBottles[i], 0, Constants.CategoryWeapon);
        }

        // Place the final bottle at the altus merchant
        //int targetShopLineupPerfumeBottle = 100725;
        //editor.SetShopLineupEquipId(targetShopLineupPerfumeBottle, perfumeBottleIDs[replacementIndexes[replacementIndexes.Length - 1]]);
        //editor.SetShopLineupEquipType(targetShopLineupPerfumeBottle, Constants.EquipTypeWeapon);
    }

    private void InitBaseplusShop(ParamsEditor editor)
    {
        using DebugTimer _ = new DebugTimer("InitBaseplusShop");

        List<ShopItemModel> shopItems = CsvReaderUtils.Read<ShopItemModel>($"{Constants.Misc}/base/shop_talismans.csv");

        // Setup the Runes shop.
        int currentShopLineupId = 9300000;
        uint currentEventFlagID = 1056448000;
        uint eventFlagStepSize = 10;
        for (int i = 0; i < shopItems.Count; i++)
        {
            int shopLineupId = currentShopLineupId++;
            string name = $"[Bernie Bingo - {shopItems[i].Type}] {shopItems[i].Name}";
            int equipID = shopItems[i].ID;
            byte equipType = shopItems[i].EquipType;
            int sellPrice = shopItems[i].Cost;
            short sellQuantity = shopItems[i].SellQuantity;
            uint eventFlagForQuantity;
            if (shopItems[i].EventFlagID == null)
            {
                eventFlagForQuantity = currentEventFlagID;
                currentEventFlagID += eventFlagStepSize;
            }
            else
            {
                eventFlagForQuantity = (uint)shopItems[i].EventFlagID!;
            }
            editor.CreateNewShopLineupRow(shopLineupId, name);
            editor.SetShopLineupEquipId(shopLineupId, equipID);
            editor.SetShopLineupEquipType(shopLineupId, equipType);
            editor.SetShopLineupSellPrice(shopLineupId, sellPrice);
            editor.SetShopLineupEventFlagForStock(shopLineupId, eventFlagForQuantity);
            editor.SetShopLineupSellQuantity(shopLineupId, sellQuantity);
        }
    }

    public void GivePidiaMagicScorpionCharm(ParamsEditor editor)
    {
        // Replace an item in Seluvis's shop with Magic Scorpion Charm
        int pidiaOldFangId = 100329;
        int magicScorpionCharmId = 2000;
        byte talismanEquipType = 2;
        uint magicScorpionAcquisitionFlag = 400141;
        short sellQuantity = 1;
        int sellPrice = 5000;
        editor.SetShopLineupEquipId(pidiaOldFangId, magicScorpionCharmId);
        editor.SetShopLineupEquipType(pidiaOldFangId, talismanEquipType);
        editor.SetShopLineupEventFlagForStock(pidiaOldFangId, magicScorpionAcquisitionFlag);
        editor.SetShopLineupSellQuantity(pidiaOldFangId, sellQuantity);
        editor.SetShopLineupSellPrice(pidiaOldFangId, sellPrice);
    }

    public void SwapTreeSentinelDrops(ParamsEditor editor)
    {
        int treeSentinelItemLotId = 30100;
        int treeSentinelDuoItemLotId = 30335;

        int halberdId = editor.GetItemLotMapLotItemId(treeSentinelItemLotId, 0);
        int halberdCategory = editor.GetItemLotMapCategory(treeSentinelItemLotId, 0);

        int greatshieldId = editor.GetItemLotMapLotItemId(treeSentinelDuoItemLotId, 0);
        int greatshieldCategory = editor.GetItemLotMapCategory(treeSentinelDuoItemLotId, 0);

        editor.SetItemLotMapLotItemId(treeSentinelItemLotId, 0, greatshieldId);
        editor.SetItemLotMapCategory(treeSentinelItemLotId, 0, greatshieldCategory);

        editor.SetItemLotMapLotItemId(treeSentinelDuoItemLotId, 0, halberdId);
        editor.SetItemLotMapCategory(treeSentinelDuoItemLotId, 0, halberdCategory);
    }

    public void DisableSerpentHunter(ParamsEditor editor)
    {
        int serpentHunterId = 17030000;

        editor.SetEquipWeaponIsCustom(serpentHunterId, 0);
        editor.SetEquipWeaponMaterialSetId(serpentHunterId, 0);
        editor.SetEquipWeaponReinforceTypeId(serpentHunterId, 3000);
        editor.SetEquipWeaponReinforceShopCategory(serpentHunterId, 0);
    }

    private void ModeCustomization(BaseMode mode)
    {


        string targetEventPath = Path.Combine(Constants.ModEngineWorkingDirectory, "base", "event");
        string targetBasePath = Path.Combine(Constants.ModEngineWorkingDirectory, "base");

        string eventDefaultPath = Path.Combine(Constants.ModeFolders, "event_base");
        string eventBaseplusPath = Path.Combine(Constants.ModeFolders, "full_baseplus");

        modeService.UpdateFolder(
            targetPath: mode == BaseMode.Baseplus
                ? targetBasePath
                : targetEventPath,

            sourcePath: mode switch
            {
                BaseMode.Baseplus => eventBaseplusPath,
                _ => eventDefaultPath
            }
        );
    }
}