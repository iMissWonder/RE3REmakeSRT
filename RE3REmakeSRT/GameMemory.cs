﻿using ProcessMemory;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Linq;

namespace RE3REmakeSRT
{
    public class GameMemory : IDisposable
    {
        // Private Variables
        private ProcessMemory.ProcessMemory memoryAccess;
        public bool HasScanned { get; private set; }
        public bool ProcessRunning => memoryAccess.ProcessRunning;
        public int ProcessExitCode => memoryAccess.ProcessExitCode;
        private const string IGT_TIMESPAN_STRING_FORMAT = @"hh\:mm\:ss\.fff";

        // Pointer Address Variables
        private long pointerAddressIGT;
        private long pointerAddressRank;
        private long pointerAddressHP;
        private long pointerAddressInventory;
        private long pointerAddressEnemy;
        private long pointerAddressPoison;

        // Pointer Classes
        private long BaseAddress { get; set; }
        private MultilevelPointer PointerIGT { get; set; }
        private MultilevelPointer PointerRank { get; set; }
        private MultilevelPointer PointerPlayerHP { get; set; }
        private MultilevelPointer PointerPlayerPoison { get; set; }
        private MultilevelPointer PointerEnemyEntryCount { get; set; }
        private MultilevelPointer[] PointerEnemyEntries { get; set; }
        private MultilevelPointer[] PointerInventoryEntries { get; set; }

        // Public Properties
        public int PlayerCurrentHealth { get; private set; }
        public int PlayerMaxHealth { get; private set; }
        public bool PlayerPoisoned { get; private set; }
        public InventoryEntry[] PlayerInventory { get; private set; }
        public int EnemyTableCount { get; private set; }
        public EnemyHP[] EnemyHealth { get; private set; }
        public long IGTRunningTimer { get; private set; }
        public long IGTCutsceneTimer { get; private set; }
        public long IGTMenuTimer { get; private set; }
        public long IGTPausedTimer { get; private set; }
        public int Rank { get; private set; }
        public float RankScore { get; private set; }

        // Public Properties - Calculated
        public long IGTCalculated => unchecked(IGTRunningTimer - IGTCutsceneTimer - IGTPausedTimer);
        public long IGTCalculatedTicks => unchecked(IGTCalculated * 10L);
        public TimeSpan IGTTimeSpan
        {
            get
            {
                TimeSpan timespanIGT;

                if (IGTCalculatedTicks <= TimeSpan.MaxValue.Ticks)
                    timespanIGT = new TimeSpan(IGTCalculatedTicks);
                else
                    timespanIGT = new TimeSpan();

                return timespanIGT;
            }
        }
        public string IGTFormattedString => IGTTimeSpan.ToString(IGT_TIMESPAN_STRING_FORMAT, CultureInfo.InvariantCulture);

        /// <summary>
        /// 
        /// </summary>
        /// <param name="proc"></param>
        public GameMemory(int pid)
        {
            memoryAccess = new ProcessMemory.ProcessMemory(pid);
            BaseAddress = NativeWrappers.GetProcessBaseAddress(pid, ProcessMemory.PInvoke.ListModules.LIST_MODULES_64BIT).ToInt64(); // Bypass .NET's managed solution for getting this and attempt to get this info ourselves via PInvoke since some users are getting 299 PARTIAL COPY when they seemingly shouldn't. This is built as x64 only and RE3 is x64 only to my knowledge.
            SelectPointerAddresses();

            // Setup the pointers.
            PointerIGT = new MultilevelPointer(memoryAccess, BaseAddress + pointerAddressIGT, 0x60L); // *
            PointerRank = new MultilevelPointer(memoryAccess, BaseAddress + pointerAddressRank); // *
            PointerPlayerHP = new MultilevelPointer(memoryAccess, BaseAddress + pointerAddressHP, 0x50L, 0x20L); // *
            PointerPlayerPoison = new MultilevelPointer(memoryAccess, BaseAddress + pointerAddressPoison, 0x50L, 0x20L, 0xF8L);

            PointerEnemyEntryCount = new MultilevelPointer(memoryAccess, BaseAddress + pointerAddressEnemy, 0x30L); // *
            GenerateEnemyEntries();

            PointerInventoryEntries = new MultilevelPointer[20];
            for (long i = 0; i < PointerInventoryEntries.Length; ++i)
                PointerInventoryEntries[i] = new MultilevelPointer(memoryAccess, BaseAddress + pointerAddressInventory, 0x50L, 0x98L, 0x10L, 0x20L + (i * 0x08L), 0x18L); // *

            // Initialize variables to default values.
            PlayerCurrentHealth = 0;
            PlayerMaxHealth = 0;
            PlayerPoisoned = false;
            PlayerInventory = new InventoryEntry[20];
            EnemyHealth = new EnemyHP[32];
            IGTRunningTimer = 0L;
            IGTCutsceneTimer = 0L;
            IGTMenuTimer = 0L;
            IGTPausedTimer = 0L;
            Rank = 0;
            RankScore = 0f;
        }

        private void SelectPointerAddresses()
        {
            if (Program.gameHash.SequenceEqual(Program.BIO3Z_Hash)) // Japanese CERO Z, latest build.
            {
                pointerAddressIGT = 0x08CE8430;
                pointerAddressRank = 0x08CB62A8;
                pointerAddressHP = 0x08CBA618;
                pointerAddressInventory = 0x08CBA618;
                pointerAddressEnemy = 0x08CB8618;

                pointerAddressPoison = 0x08DCB6C0; // Not actually used right now.
            }
            else // World-wide, latest build.
            {
                pointerAddressIGT = 0x08DAA3F0;
                pointerAddressRank = 0x08D78258;
                pointerAddressHP = 0x08D7C5E8;
                pointerAddressInventory = 0x08D7C5E8;
                pointerAddressEnemy = 0x08D7A5A8;

                pointerAddressPoison = 0x08DCB6C0; // Not actually used right now.
            }
        }

        /// <summary>
        /// Dereferences a 4-byte signed integer via the PointerEnemyEntryCount pointer to detect how large the enemy pointer table is and then create the pointer table entries if required.
        /// </summary>
        private void GenerateEnemyEntries()
        {
            EnemyTableCount = PointerEnemyEntryCount.DerefInt(0x1CL); // Get the size of the enemy pointer table. This seems to double (4, 8, 16, 32, ...) but never decreases, even after a new game is started.
            if (PointerEnemyEntries == null || PointerEnemyEntries.Length != EnemyTableCount) // Enter if the pointer table is null (first run) or the size does not match.
            {
                PointerEnemyEntries = new MultilevelPointer[EnemyTableCount]; // Create a new enemy pointer table array with the detected size.
                for (long i = 0; i < PointerEnemyEntries.Length; ++i) // Loop through and create all of the pointers for the table.
                    PointerEnemyEntries[i] = new MultilevelPointer(memoryAccess, BaseAddress + pointerAddressEnemy, 0x30L, 0x20L + (i * 0x08L), 0x300L);
            }
        }

        /// <summary>
        /// 
        /// </summary>
        public void UpdatePointers()
        {
            PointerIGT.UpdatePointers();
            PointerPlayerHP.UpdatePointers();
            PointerPlayerPoison.UpdatePointers();
            PointerRank.UpdatePointers();

            PointerEnemyEntryCount.UpdatePointers();
            GenerateEnemyEntries(); // This has to be here for the next part.
            for (int i = 0; i < PointerEnemyEntries.Length; ++i)
                PointerEnemyEntries[i].UpdatePointers();

            for (int i = 0; i < PointerInventoryEntries.Length; ++i)
                PointerInventoryEntries[i].UpdatePointers();
        }

        /// <summary>
        /// This call refreshes important variables such as IGT.
        /// </summary>
        /// <param name="cToken"></param>
        public void RefreshSlim()
        {
            // IGT
            IGTRunningTimer = PointerIGT.DerefLong(0x18);
            IGTCutsceneTimer = PointerIGT.DerefLong(0x20);
            IGTMenuTimer = PointerIGT.DerefLong(0x28);
            IGTPausedTimer = PointerIGT.DerefLong(0x30);
        }

        /// <summary>
        /// This call refreshes everything. This should be used less often. Inventory rendering can be more expensive and doesn't change as often.
        /// </summary>
        /// <param name="cToken"></param>
        public void Refresh()
        {
            // Perform slim lookups first.
            RefreshSlim();

            // Other lookups that don't need to update as often.
            // Player HP
            PlayerMaxHealth = PointerPlayerHP.DerefInt(0x54);
            PlayerCurrentHealth = PointerPlayerHP.DerefInt(0x58);
            PlayerPoisoned = PointerPlayerPoison.DerefByte(0x258) == 0x01;
            Rank = PointerRank.DerefInt(0x58);
            RankScore = PointerRank.DerefFloat(0x5C);

            // Enemy HP
            GenerateEnemyEntries();
            for (int i = 0; i < PointerEnemyEntries.Length; ++i)
                EnemyHealth[i] = new EnemyHP(PointerEnemyEntries[i].DerefInt(0x54), PointerEnemyEntries[i].DerefInt(0x58));

            // Inventory
            for (int i = 0; i < PointerInventoryEntries.Length; ++i)
            {
                long invDataPointer = PointerInventoryEntries[i].DerefLong(0x10);
                long invDataOffset = invDataPointer - PointerInventoryEntries[i].Address;
                int invSlot = PointerInventoryEntries[i].DerefInt(0x28);
                byte[] invData = PointerInventoryEntries[i].DerefByteArray(invDataOffset + 0x10, 0x14);
                PlayerInventory[i] = new InventoryEntry(invSlot, (invData != null) ? invData : new byte[20] { 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00 });
            }

            HasScanned = true;
        }

        #region IDisposable Support
        private bool disposedValue = false; // To detect redundant calls

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    // TODO: dispose managed state (managed objects).
                    if (memoryAccess != null)
                        memoryAccess.Dispose();
                }

                // TODO: free unmanaged resources (unmanaged objects) and override a finalizer below.
                // TODO: set large fields to null.

                disposedValue = true;
            }
        }

        // TODO: override a finalizer only if Dispose(bool disposing) above has code to free unmanaged resources.
        // ~REmake1Memory() {
        //   // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
        //   Dispose(false);
        // }

        // This code added to correctly implement the disposable pattern.
        public void Dispose()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            Dispose(true);
            // TODO: uncomment the following line if the finalizer is overridden above.
            // GC.SuppressFinalize(this);
        }
        #endregion
    }
}
