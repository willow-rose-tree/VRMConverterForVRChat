using System;
using System.Reflection;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEditor;
using VRM;
using UniGLTF;
using VRCSDK2;

namespace Esperecyan.Unity.VRMConverterForVRChat
{
    /// <summary>
    /// 揺れ物に関する設定。
    /// </summary>
    public class SwayingObjectsConverter
    {
        private static readonly Type DynamicBoneType = Type.GetType("DynamicBone, Assembly-CSharp");
        private static readonly Type DynamicBoneColliderType = Type.GetType("DynamicBoneCollider, Assembly-CSharp");
        private static readonly Type DynamicBoneColliderBaseListType
            = typeof(List<>).MakeGenericType(Type.GetType("DynamicBoneColliderBase, Assembly-CSharp"));

        internal static IEnumerable<Converter.Message> Apply(
            GameObject avatar,
            ComponentsReplacer.SwayingObjectsConverterSetting setting,
            ComponentsReplacer.SwayingParametersConverter swayingParametersConverter
        )
        {
            if (setting == ComponentsReplacer.SwayingObjectsConverterSetting.RemoveSwayingObjects)
            {
                return new List<Converter.Message>();
            }

            IDictionary<VRMSpringBoneColliderGroup, IEnumerable<MonoBehaviour>> dynamicBoneColliderGroups = null;
            if (setting == ComponentsReplacer.SwayingObjectsConverterSetting.ConvertVrmSpringBonesAndVrmSpringBoneColliderGroups)
            {
                RemoveUnusedColliderGroups(avatar: avatar);
                dynamicBoneColliderGroups = ConvertVRMSpringBoneColliderGroups(avatar);
            }
            ConvertVRMSpringBones(
                avatar: avatar,
                dynamicBoneColliderGroups: dynamicBoneColliderGroups,
                swayingParametersConverter: swayingParametersConverter
            );

            return GetMessagesAboutDynamicBoneLimits(avatar: avatar);
        }

        /// <summary>
        /// <see cref="AvatarPerformance"/>クラスを書き替えて、DynamicBoneに関するパフォーマンスが表示されないVRChat SDKのバグを修正します。
        /// </summary>
        /// <seealso cref="SwayingObjectsConverter.GetMessagesAboutDynamicBoneLimits"/>
        /// <remarks>
        /// 参照:
        /// SDK avatar performance reports always report dynamic bone counts as 0 | Bug Reports | VRChat
        /// <https://vrchat.canny.io/bug-reports/p/sdk-avatar-performance-reports-always-report-dynamic-bone-counts-as-0>
        /// </remarks>
        [InitializeOnLoadMethod]
        private static void FixFindDynamicBoneTypesMethodOnAvatarPerformanceClass()
        {
            string fullPath = UnityPath.FromUnityPath(VRChatUtility.AvatarPerformanceClassPath).FullPath;

            string content = File.ReadAllText(path: fullPath, encoding: Encoding.UTF8);
            if (content.Contains("DynamicBoneColliderBase"))
            {
                return;
            }

            string fixedContent = content.Replace(
                oldValue: "System.Type dyBoneColliderType = Validation.GetTypeFromName(\"DynamicBoneCollider\");",
                newValue: "System.Type dyBoneColliderType = Validation.GetTypeFromName(\"DynamicBoneColliderBase\");"
            );
            if (fixedContent == content)
            {
                return;
            }

            File.WriteAllText(path: fullPath, contents: fixedContent, encoding: Encoding.UTF8);
        }

        /// <summary>
        /// <see cref="VRMSpringBone.ColliderGroups"/>から参照されていない<see cref="VRMSpringBoneColliderGroup"/>を削除します。
        /// </summary>
        /// <param name="avatar"></param>
        private static void RemoveUnusedColliderGroups(GameObject avatar)
        {
            IEnumerable<GameObject> objectsHavingUsedColliderGroup = avatar.GetComponentsInChildren<VRMSpringBone>()
                .SelectMany(springBone => springBone.ColliderGroups)
                .Select(colliderGroup => colliderGroup.gameObject)
                .ToArray();

            foreach (var colliderGroup in avatar.GetComponentsInChildren<VRMSpringBoneColliderGroup>())
            {
                if (!objectsHavingUsedColliderGroup.Contains(colliderGroup.gameObject))
                {
                    UnityEngine.Object.DestroyImmediate(colliderGroup);
                }
            }
        }

        /// <summary>
        /// 子孫の<see cref="VRMSpringBoneColliderGroup"/>を基に<see cref="DynamicBoneCollider"/>を設定します。
        /// </summary>
        /// <param name="avatar"></param>
        /// <returns>キーに<see cref="VRMSpringBoneColliderGroup"/>、値に対応する<see cref="DynamicBoneCollider"/>のリストを持つジャグ配列。</returns>
        private static IDictionary<VRMSpringBoneColliderGroup, IEnumerable<MonoBehaviour>> ConvertVRMSpringBoneColliderGroups(GameObject avatar)
        {
            return avatar.GetComponentsInChildren<VRMSpringBoneColliderGroup>().ToDictionary(
                keySelector: colliderGroup => colliderGroup,
                elementSelector: colliderGroup => ConvertVRMSpringBoneColliderGroup(colliderGroup: colliderGroup)
            );
        }

        /// <summary>
        /// 指定された<see cref="VRMSpringBoneColliderGroup"/>を基に<see cref="DynamicBoneCollider"/>を設定します。
        /// </summary>
        /// <param name="colliderGroup"></param>
        /// <param name="bonesForCollisionWithOtherAvatar"></param>
        /// <returns>生成した<see cref="DynamicBoneCollider"/>のリスト。</returns>
        private static IEnumerable<MonoBehaviour> ConvertVRMSpringBoneColliderGroup(
            VRMSpringBoneColliderGroup colliderGroup
        )
        {
            var bone = colliderGroup.gameObject;

            return colliderGroup.Colliders.Select(collider => {
                var dynamicBoneCollider = bone.AddComponent(DynamicBoneColliderType);
                DynamicBoneColliderType.GetField("m_Center").SetValue(dynamicBoneCollider, collider.Offset);
                DynamicBoneColliderType.GetField("m_Radius").SetValue(dynamicBoneCollider, collider.Radius);
                return dynamicBoneCollider as MonoBehaviour;
            });
        }

        /// <summary>
        /// 子孫の<see cref="VRMSpringBone"/>を基に<see cref="DynamicBone"/>を設定します。
        /// </summary>
        /// <param name="avatar"></param>
        /// <param name="dynamicBoneColliderGroups">キーに<see cref="VRMSpringBoneColliderGroup"/>、値に対応する<see cref="DynamicBoneCollider"/>のリストを持つジャグ配列。</param>
        /// <param name="swayingParametersConverter"></param>
        private static void ConvertVRMSpringBones(
            GameObject avatar,
            IDictionary<VRMSpringBoneColliderGroup, IEnumerable<MonoBehaviour>> dynamicBoneColliderGroups,
            ComponentsReplacer.SwayingParametersConverter swayingParametersConverter
        )
        {
            foreach (var springBone in avatar.GetComponentsInChildren<VRMSpringBone>())
            {
                ConvertVRMSpringBone(springBone: springBone, dynamicBoneColliderGroups: dynamicBoneColliderGroups, swayingParametersConverter: swayingParametersConverter);
            }
        }

        /// <summary>
        /// 指定された<see cref="VRMSpringBone"/>を基に<see cref="DynamicBone"/>を設定します。
        /// </summary>
        /// <param name="springBone"></param>
        /// <param name="dynamicBoneColliderGroups">キーに<see cref="VRMSpringBoneColliderGroup"/>、値に対応する<see cref="DynamicBoneCollider"/>のリストを持つジャグ配列。</param>
        /// <param name="swayingParametersConverter"></param>
        private static void ConvertVRMSpringBone(
            VRMSpringBone springBone,
            IDictionary<VRMSpringBoneColliderGroup, IEnumerable<MonoBehaviour>> dynamicBoneColliderGroups,
            ComponentsReplacer.SwayingParametersConverter swayingParametersConverter
        )
        {
            var springBoneParameters = new SpringBoneParameters(stiffnessForce: springBone.m_stiffnessForce, dragForce: springBone.m_dragForce);
            var boneInfo = new BoneInfo(vrmMeta: springBone.gameObject.GetComponentsInParent<VRMMeta>()[0]);

            foreach (var transform in springBone.RootBones)
            {
                var dynamicBone = springBone.gameObject.AddComponent(DynamicBoneType);
                DynamicBoneType.GetField("m_Root").SetValue(dynamicBone, transform);
                DynamicBoneType.GetField("m_Exclusions").SetValue(dynamicBone, new List<Transform>());

                DynamicBoneParameters dynamicBoneParameters = null;
                if (swayingParametersConverter != null)
                {
                    dynamicBoneParameters = swayingParametersConverter(
                        springBoneParameters: springBoneParameters,
                        boneInfo: boneInfo
                    );
                }
                if (dynamicBoneParameters != null)
                {
                    DynamicBoneType.GetField("m_Damping").SetValue(dynamicBone, dynamicBoneParameters.Damping);
                    DynamicBoneType.GetField("m_DampingDistrib")
                        .SetValue(dynamicBone, dynamicBoneParameters.DampingDistrib);
                    DynamicBoneType.GetField("m_Elasticity").SetValue(dynamicBone, dynamicBoneParameters.Elasticity);
                    DynamicBoneType.GetField("m_ElasticityDistrib")
                        .SetValue(dynamicBone, dynamicBoneParameters.ElasticityDistrib);
                    DynamicBoneType.GetField("m_Stiffness").SetValue(dynamicBone, dynamicBoneParameters.Stiffness);
                    DynamicBoneType.GetField("m_StiffnessDistrib")
                        .SetValue(dynamicBone, dynamicBoneParameters.StiffnessDistrib);
                    DynamicBoneType.GetField("m_Inert").SetValue(dynamicBone, dynamicBoneParameters.Inert);
                    DynamicBoneType.GetField("m_InertDistrib")
                        .SetValue(dynamicBone, dynamicBoneParameters.InertDistrib);
                }

                DynamicBoneType.GetField("m_Gravity")
                    .SetValue(dynamicBone, springBone.m_gravityDir * springBone.m_gravityPower);
                if (dynamicBoneColliderGroups != null)
                {
                    var colliders = Activator.CreateInstance(type: DynamicBoneColliderBaseListType);
                    MethodInfo addMethod = DynamicBoneColliderBaseListType.GetMethod("Add");
                    foreach (var collider in springBone.ColliderGroups.SelectMany(
                            colliderGroup => dynamicBoneColliderGroups[colliderGroup]
                    ))
                    {
                        addMethod.Invoke(colliders, new[] { collider });
                    }
                    DynamicBoneType.GetField("m_Colliders").SetValue(dynamicBone, colliders);
                }
            }
        }


        /// <summary>
        /// DynamicBoneの制限の既定値を超えていた場合、警告メッセージを返します。
        /// </summary>
        /// <param name="prefabInstance"></param>
        /// <returns></returns>
        private static IEnumerable<Converter.Message> GetMessagesAboutDynamicBoneLimits(GameObject avatar)
        {
            var messages = new List<Converter.Message>();
            AvatarPerformanceStats statistics = AvatarPerformance.CalculatePerformanceStats(
                avatarName: avatar.GetComponent<VRMMeta>().Meta.Title,
                avatarObject: avatar
            );Debug.Log(statistics.DynamicBoneAffectedTransformCount);

            if (statistics.DynamicBoneAffectedTransformCount
                > AvatarPerformanceStats.MediumPeformanceStatLimits.DynamicBoneAffectedTransformCount)
            {
                messages.Add(new Converter.Message
                {
                    message = string.Format(
                        Gettext._("The “Dynamic Bone Affected Transform Count” is {0}."),
                        statistics.DynamicBoneAffectedTransformCount
                    ) + string.Format(
                        Gettext._("If this value exceeds {0}, the default user setting disable all Dynamic Bones."),
                        AvatarPerformanceStats.MediumPeformanceStatLimits.DynamicBoneAffectedTransformCount
                    ),
                    type = MessageType.Warning,
                });
            }
            
            if (statistics.DynamicBoneCollisionCheckCount
                > AvatarPerformanceStats.MediumPeformanceStatLimits.DynamicBoneCollisionCheckCount)
            {
                messages.Add(new Converter.Message
                {
                    message = string.Format(
                        Gettext._("The “Dynamic Bone Collision Check Count” is {0}."),
                        statistics.DynamicBoneCollisionCheckCount
                    ) + string.Format(
                        Gettext._("If this value exceeds {0}, the default user setting disable all Dynamic Bones."),
                        AvatarPerformanceStats.MediumPeformanceStatLimits.DynamicBoneCollisionCheckCount
                    ),
                    type = MessageType.Warning,
                });
            }

            return messages;
        }
    }
}
