using UnityEngine;
using Game.Data;

namespace Game.Runtime
{
    public sealed class PlayerController : MonoBehaviour
    {
        [SerializeField] private GameConfig config = null!;

        public int GetConfiguredHealth()
        {
            return config.MaxHealth;
        }
    }
}
