using UnityEngine;

namespace Game.Data
{
    [CreateAssetMenu(menuName = "Game/Game Config", fileName = "GameConfig")]
    public sealed class GameConfig : ScriptableObject
    {
        [SerializeField] private int maxHealth = 100;
        [SerializeField] private float moveSpeed = 5f;

        public int MaxHealth => maxHealth;
        public float MoveSpeed => moveSpeed;
    }
}
