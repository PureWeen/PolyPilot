#include <stdlib.h>

static void xamarin_initialize_dotnet ()
{
	setenv ("DOTNET_MODIFIABLE_ASSEMBLIES", "debug", 1);
}

extern "C" void xamarin_initialize_dotnet();
extern "C" void xamarin_create_classes_Microsoft_MacCatalyst();

static void xamarin_invoke_registration_methods ()
{
	xamarin_initialize_dotnet();
	xamarin_create_classes_Microsoft_MacCatalyst();
}

#include "xamarin/xamarin.h"

extern void *mono_aot_module_System_Private_CoreLib_info;

void xamarin_register_modules_impl ()
{
	mono_aot_register_module (mono_aot_module_System_Private_CoreLib_info);

}

void xamarin_register_assemblies_impl ()
{
	GCHandle exception_gchandle = INVALID_GCHANDLE;

}

extern "C" { void mono_ee_interp_init (const char *); }
extern "C" { void mono_icall_table_init (void); }
extern "C" { void mono_marshal_ilgen_init (void); }
extern "C" { void mono_method_builder_ilgen_init (void); }
extern "C" { void mono_sgen_mono_ilgen_init (void); }
static const char *xamarin_runtime_libraries_array[] = {
	"libSystem.Globalization.Native",
	"libSystem.IO.Compression.Native",
	"libSystem.Native",
	"libSystem.Net.Security.Native",
	"libSystem.Security.Cryptography.Native.Apple",
	"libbrotlicommon",
	"libbrotlidec",
	"libbrotlienc",
	"libmono-component-debugger-static",
	"libmono-component-diagnostics_tracing-stub-static",
	"libmono-component-hot_reload-static",
	"libmono-component-marshal-ilgen-static",
	"libmonosgen-2.0",
	"libmono-component-debugger-static",
	"libmono-component-hot_reload-static",
	"libmono-component-marshal-ilgen-static",
	"libmono-component-diagnostics_tracing-stub-static",
	NULL
};
void xamarin_setup_impl ()
{
	mono_icall_table_init ();
	mono_marshal_ilgen_init ();
	mono_method_builder_ilgen_init ();
	mono_sgen_mono_ilgen_init ();
	mono_jit_set_aot_mode (MONO_AOT_MODE_INTERP);
	xamarin_invoke_registration_methods ();
	xamarin_libmono_native_link_mode = XamarinNativeLinkModeStaticObject;
	xamarin_runtime_libraries = xamarin_runtime_libraries_array;
	xamarin_gc_pump = FALSE;
	xamarin_init_mono_debug = TRUE;
	xamarin_executable_name = "AutoPilot.App.dll";
	mono_use_llvm = FALSE;
	xamarin_log_level = 0;
	xamarin_arch_name = "arm64";
	xamarin_marshal_objectivec_exception_mode = MarshalObjectiveCExceptionModeThrowManagedException;
	xamarin_debug_mode = TRUE;
	setenv ("MONO_GC_PARAMS", "major=marksweep", 1);
	xamarin_supports_dynamic_registration = TRUE;
	xamarin_runtime_configuration_name = "runtimeconfig.bin";
}

int main (int argc, char **argv)
{
	NSAutoreleasePool *pool = [[NSAutoreleasePool alloc] init];
	int rv = xamarin_main (argc, argv, XamarinLaunchModeApp);
	[pool drain];
	return rv;
}

void xamarin_initialize_callbacks () __attribute__ ((constructor));
void xamarin_initialize_callbacks ()
{
	xamarin_setup = xamarin_setup_impl;
	xamarin_register_assemblies = xamarin_register_assemblies_impl;
	xamarin_register_modules = xamarin_register_modules_impl;
}
