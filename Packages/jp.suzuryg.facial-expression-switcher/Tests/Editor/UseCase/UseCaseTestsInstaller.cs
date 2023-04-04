﻿using Suzuryg.FacialExpressionSwitcher.Domain;
using Suzuryg.FacialExpressionSwitcher.UseCase.ModifyMenu;
using Suzuryg.FacialExpressionSwitcher.UseCase.ModifyMenu.ModifyMode;
using Suzuryg.FacialExpressionSwitcher.UseCase.ModifyMenu.ModifyMode.ModifyAnimation;
using Suzuryg.FacialExpressionSwitcher.UseCase.ModifyMenu.ModifyMode.ModifyBranch;
using Suzuryg.FacialExpressionSwitcher.Detail.Data;
using Zenject;

namespace Suzuryg.FacialExpressionSwitcher.UseCase
{
    public class UseCaseTestsInstaller
    {
        public DiContainer Container { get; } = new DiContainer();

        public UseCaseTestsInstaller()
        {
            Container.Bind<IAnimationEditor>().To<MockAnimationEditor>().AsTransient();

            if (UseCaseTestConstants.UseActualRepository)
            {
                // Re-use usecase tests for menu-repository's test.
                Container.Bind<SerializableMenu>().FromNewComponentOnNewGameObject().AsTransient();
                Container.Bind<IMenuRepository>().To<MenuRepository>().AsSingle();
            }
            else
            {
                Container.Bind<IMenuRepository>().To<InMemoryMenuRepository>().AsSingle();
            }

            Container.Bind<CreateMenuUseCase>().AsTransient();
            Container.Bind<AddMenuItemUseCase>().AsTransient();
            Container.Bind<ModifyModePropertiesUseCase>().AsTransient();
            Container.Bind<ModifyGroupPropertiesUseCase>().AsTransient();
            Container.Bind<MoveMenuItemUseCase>().AsTransient();
            Container.Bind<RemoveMenuItemUseCase>().AsTransient();
            Container.Bind<AddBranchUseCase>().AsTransient();
            Container.Bind<ModifyBranchPropertiesUseCase>().AsTransient();
            Container.Bind<ChangeBranchOrderUseCase>().AsTransient();
            Container.Bind<RemoveBranchUseCase>().AsTransient();
            Container.Bind<AddConditionUseCase>().AsTransient();
            Container.Bind<ChangeConditionOrderUseCase>().AsTransient();
            Container.Bind<ModifyConditionUseCase>().AsTransient();
            Container.Bind<RemoveConditionUseCase>().AsTransient();
            Container.Bind<SetNewAnimationUseCase>().AsTransient();
            Container.Bind<SetExistingAnimationUseCase>().AsTransient();

            Container.Bind<UpdateMenuSubject>().AsSingle();
            Container.Bind<ICreateMenuPresenter>().To<MockCreateMenuPresenter>().AsSingle();
            Container.Bind<IAddMenuItemPresenter>().To<MockAddMenuItemPresenter>().AsSingle();
            Container.Bind<IModifyModePropertiesPresenter>().To<MockModifyModePropertiesPresenter>().AsSingle();
            Container.Bind<IModifyGroupPropertiesPresenter>().To<MockModifyGroupPropertiesPresenter>().AsSingle();
            Container.Bind<IMoveMenuItemPresenter>().To<MockMoveMenuItemPresenter>().AsSingle();
            Container.Bind<IRemoveMenuItemPresenter>().To<MockRemoveMenuItemPresenter>().AsSingle();
            Container.Bind<IAddBranchPresenter>().To<MockAddBranchPresenter>().AsSingle();
            Container.Bind<IModifyBranchPropertiesPresenter>().To<MockModifyBranchPropertiesPresenter>().AsSingle();
            Container.Bind<IChangeBranchOrderPresenter>().To<MockChangeBranchOrderPresenter>().AsSingle();
            Container.Bind<IRemoveBranchPresenter>().To<MockRemoveBranchPresenter>().AsSingle();
            Container.Bind<IAddConditionPresenter>().To<MockAddConditionPresenter>().AsSingle();
            Container.Bind<IChangeConditionOrderPresenter>().To<MockChangeConditionOrderPresenter>().AsSingle();
            Container.Bind<IModifyConditionPresenter>().To<MockModifyConditionPresenter>().AsSingle();
            Container.Bind<IRemoveConditionPresenter>().To<MockRemoveConditionPresenter>().AsSingle();
            Container.Bind<ISetNewAnimationPresenter>().To<MockSetNewAnimationPresenter>().AsSingle();
            Container.Bind<ISetExistingAnimationPresenter>().To<MockSetExistingAnimationPresenter>().AsSingle();
        }
    }
}
