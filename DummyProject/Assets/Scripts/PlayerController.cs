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

namespace Game.Runtime
{
    public interface IDamageable
    {
        void ApplyDamage(int amount);
    }

    public sealed class Enemy : IDamageable
    {
        public void ApplyDamage(int amount)
        {
            Report(amount);
        }

        private void Report(int amount)
        {
        }
    }

    public static class EnemyFactory
    {
        public static Enemy Create()
        {
            var enemy = new Enemy();
            enemy.ApplyDamage(1);
            return enemy;
        }
    }
}
