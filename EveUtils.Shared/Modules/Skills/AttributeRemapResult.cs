using System;

namespace EveUtils.Shared.Modules.Skills;

/// <summary>The fastest base-attribute distribution found for a set of rows, and the total training time it gives
/// them (implants included).</summary>
public readonly record struct AttributeRemapResult(CharacterAttributeSet BaseAttributes, TimeSpan TotalTime);
