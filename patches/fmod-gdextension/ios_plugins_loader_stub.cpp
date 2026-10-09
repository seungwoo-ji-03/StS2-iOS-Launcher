// Static FMOD plugin loader for iOS (copied to src/plugins/ by tools/build_natives.sh).
// Upstream generates this file from project settings during a Godot editor export. Building the
// extension on its own skips that step, so this minimal version stands in. Slay the Spire 2 uses no
// extra static FMOD codec/DSP/output plugins, so it reports zero.
#ifdef IOS_ENABLED
#include <cstdint>
#include <cstdlib>
#include "fmod.h"
#include "ios_plugins_loader.h"

extern "C" __attribute__((visibility("default"))) __attribute__((used))
uint32_t* load_all_fmod_plugins(FMOD_IOS_INTERFACE* /*p_interface*/, uint32_t* r_count) {
    *r_count = 0;
    // the caller free()s the result, so return a valid allocation
    return reinterpret_cast<uint32_t*>(std::malloc(sizeof(uint32_t)));
}
#endif
