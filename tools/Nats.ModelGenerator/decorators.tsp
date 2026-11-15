import "./emitter.mjs";

using TypeSpec.Reflection;

namespace Manufacturing.CSharp;

/** Emit a positional C# record. */
extern dec csharpRecord(target: Model, sealed: valueof boolean);
