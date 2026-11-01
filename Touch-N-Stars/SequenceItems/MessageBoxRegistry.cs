using NINA.Core.Utility.WindowService;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace TouchNStars.SequenceItems {
    /// <summary>
    /// Global registry for managing active TNS MessageBox instances
    /// Allows API access to close message boxes remotely
    /// </summary>
    public static class MessageBoxRegistry {
        private static readonly ConcurrentDictionary<Guid, MessageBoxRegistration> activeMessageBoxes = new();

        public class MessageBoxRegistration {
            public Guid Id { get; set; }
            public string Text { get; set; }
            public DateTime CreatedAt { get; set; }
            public IWindowService WindowService { get; set; }

            /// <summary>
            /// Closes the box; the argument is whether the sequence continues. The box takes the
            /// choice with the close itself, since the registration is gone once Close returns.
            /// </summary>
            public Action<bool> CloseAction { get; set; }
        }

        /// <summary>
        /// Register a new message box
        /// </summary>
        public static Guid Register(string text, IWindowService windowService, Action<bool> closeAction) {
            var id = Guid.NewGuid();
            var registration = new MessageBoxRegistration {
                Id = id,
                Text = text,
                CreatedAt = DateTime.Now,
                WindowService = windowService,
                CloseAction = closeAction
            };

            activeMessageBoxes.TryAdd(id, registration);
            return id;
        }

        /// <summary>
        /// Unregister a message box (called when it closes naturally)
        /// </summary>
        public static void Unregister(Guid id) {
            activeMessageBoxes.TryRemove(id, out _);
        }

        /// <summary>
        /// Close a specific message box by ID
        /// </summary>
        public static bool Close(Guid id, bool continueSequence = true) {
            if (activeMessageBoxes.TryGetValue(id, out var registration)) {
                registration.CloseAction?.Invoke(continueSequence);
                Unregister(id);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Close all active message boxes
        /// </summary>
        public static int CloseAll(bool continueSequence = true) {
            var count = 0;
            foreach (var kvp in activeMessageBoxes) {
                if (Close(kvp.Key, continueSequence)) {
                    count++;
                }
            }
            return count;
        }

        /// <summary>
        /// Get all active message boxes
        /// </summary>
        public static List<MessageBoxRegistration> GetAll() {
            return activeMessageBoxes.Values.ToList();
        }

        /// <summary>
        /// Get a specific message box by ID
        /// </summary>
        public static MessageBoxRegistration Get(Guid id) {
            activeMessageBoxes.TryGetValue(id, out var registration);
            return registration;
        }

        /// <summary>
        /// Check if a message box is still active
        /// </summary>
        public static bool IsActive(Guid id) {
            return activeMessageBoxes.ContainsKey(id);
        }

        /// <summary>
        /// Get count of active message boxes
        /// </summary>
        public static int Count => activeMessageBoxes.Count;
    }
}
