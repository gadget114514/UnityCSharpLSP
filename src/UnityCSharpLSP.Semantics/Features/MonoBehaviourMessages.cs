namespace UnityCSharpLSP.Semantics.Features;

public sealed record UnityMessageDefinition(
    string Name,
    string Signature,
    string Description,
    string InsertSnippet);

public static class MonoBehaviourMessages
{
    public static readonly IReadOnlyList<UnityMessageDefinition> All =
    [
        new("Awake", "void Awake()", "Awake is called when the script instance is being loaded.",
            "private void Awake()\n{\n    $0\n}"),
        new("Start", "void Start()", "Start is called on the frame when a script is enabled just before any of the Update methods are called the first time.",
            "private void Start()\n{\n    $0\n}"),
        new("Update", "void Update()", "Update is called every frame, if the MonoBehaviour is enabled.",
            "private void Update()\n{\n    $0\n}"),
        new("FixedUpdate", "void FixedUpdate()", "Frame-rate independent message for physics calculations.",
            "private void FixedUpdate()\n{\n    $0\n}"),
        new("LateUpdate", "void LateUpdate()", "LateUpdate is called after all Update functions have been called.",
            "private void LateUpdate()\n{\n    $0\n}"),
        new("OnEnable", "void OnEnable()", "This function is called when the object becomes enabled and active.",
            "private void OnEnable()\n{\n    $0\n}"),
        new("OnDisable", "void OnDisable()", "This function is called when the behaviour becomes disabled.",
            "private void OnDisable()\n{\n    $0\n}"),
        new("OnDestroy", "void OnDestroy()", "Destroying the attached Behaviour will result in receiving OnDestroy.",
            "private void OnDestroy()\n{\n    $0\n}"),
        new("OnTriggerEnter", "void OnTriggerEnter(Collider other)", "Called when another Collider enters the trigger collider.",
            "private void OnTriggerEnter(Collider other)\n{\n    $0\n}"),
        new("OnTriggerExit", "void OnTriggerExit(Collider other)", "Called when another Collider has stopped touching the trigger.",
            "private void OnTriggerExit(Collider other)\n{\n    $0\n}"),
        new("OnCollisionEnter", "void OnCollisionEnter(Collision collision)", "Called when this collider/rigidbody has begun touching another rigidbody/collider.",
            "private void OnCollisionEnter(Collision collision)\n{\n    $0\n}"),
        new("OnCollisionExit", "void OnCollisionExit(Collision collision)", "Called when this collider/rigidbody has stopped touching another rigidbody/collider.",
            "private void OnCollisionExit(Collision collision)\n{\n    $0\n}"),
        new("OnGUI", "void OnGUI()", "Called for rendering and handling GUI events.",
            "private void OnGUI()\n{\n    $0\n}"),
    ];

    public static bool InheritsFrom(Microsoft.CodeAnalysis.ITypeSymbol? type, string baseTypeName)
    {
        var current = type;
        while (current is not null)
        {
            if (string.Equals(current.Name, baseTypeName, StringComparison.Ordinal) ||
                string.Equals(current.MetadataName, baseTypeName, StringComparison.Ordinal))
            {
                return true;
            }

            current = current.BaseType;
        }

        return false;
    }
}
