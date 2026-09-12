#include <stdlib.h>
int TSGgml_HasBackendFailure(void) { return getenv("TS_DIAGNOSTIC_FAILURE_STUB") != 0; }
const char * TSGgml_GetBackendFailureText(void) { return "simulated command buffer failure details"; }
