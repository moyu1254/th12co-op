#pragma once
#include <windows.h>
#include <stdint.h>
#include <string.h>

// The 1.00b score-format fix adds 0x20 bytes before every code address used by
// this MOD. The rest of .text was compared with 1.00a after normalizing code
// references: only the startup version character differs. Globals, imports,
// vtable locations and object layouts are unchanged. See the version audit.
namespace coop_game {
static uintptr_t codeShift;
static bool steam;
static uintptr_t Code(uintptr_t address100a) { return address100a + codeShift; }

static bool SelectLayout() {
    const auto image = reinterpret_cast<unsigned char*>(GetModuleHandleW(nullptr));
    if (reinterpret_cast<uintptr_t>(image) != 0x400000) return false;
    const auto dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(image);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE || dos->e_lfanew < 0 || dos->e_lfanew > 4096) return false;
    const auto pe = reinterpret_cast<const IMAGE_NT_HEADERS32*>(image + dos->e_lfanew);
    if (pe->Signature != IMAGE_NT_SIGNATURE || pe->FileHeader.Machine != IMAGE_FILE_MACHINE_I386 ||
        pe->OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR32_MAGIC || pe->OptionalHeader.ImageBase != 0x400000 ||
        pe->FileHeader.NumberOfSections < 4 || pe->FileHeader.NumberOfSections > 5) return false;
    const auto text = IMAGE_FIRST_SECTION(pe);
    if (memcmp(text->Name, ".text", 5) || text->VirtualAddress != 0x1000 || text->SizeOfRawData != 0x96c00) return false;
    const auto entry = pe->OptionalHeader.AddressOfEntryPoint;
    const auto size = pe->OptionalHeader.SizeOfImage;
    if (entry == 0x6e7fd && size == 0xd9000 && text->Misc.VirtualSize == 0x96ab5 &&
        !memcmp(image + 0xa1314, "1.00a", 5)) {
        codeShift = 0; steam = false; return true;
    }
    if (((entry == 0x6e81d && size == 0xd9000) || (entry == 0xd9310 && size == 0xfc000)) &&
        text->Misc.VirtualSize == 0x96ad5 && !memcmp(image + 0xa1314, "1.00b", 5)) {
        codeShift = 0x20; steam = entry == 0xd9310; return true;
    }
    return false;
}
}
