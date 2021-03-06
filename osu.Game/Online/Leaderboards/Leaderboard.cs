﻿// Copyright (c) 2007-2018 ppy Pty Ltd <contact@ppy.sh>.
// Licensed under the MIT Licence - https://raw.githubusercontent.com/ppy/osu/master/LICENCE

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.Containers;
using osu.Framework.Threading;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.UserInterface;
using osu.Game.Online.API;
using osuTK;
using osuTK.Graphics;

namespace osu.Game.Online.Leaderboards
{
    public abstract class Leaderboard<TScope, ScoreInfo> : Container, IOnlineComponent
    {
        private const double fade_duration = 300;

        private readonly ScrollContainer scrollContainer;
        private readonly Container placeholderContainer;

        private FillFlowContainer<LeaderboardScore> scrollFlow;

        private readonly LoadingAnimation loading;

        private ScheduledDelegate showScoresDelegate;

        private bool scoresLoadedOnce;

        private IEnumerable<ScoreInfo> scores;

        public IEnumerable<ScoreInfo> Scores
        {
            get { return scores; }
            set
            {
                scores = value;

                scoresLoadedOnce = true;

                scrollFlow?.FadeOut(fade_duration, Easing.OutQuint).Expire();
                scrollFlow = null;

                loading.Hide();

                if (scores == null || !scores.Any())
                    return;

                // ensure placeholder is hidden when displaying scores
                PlaceholderState = PlaceholderState.Successful;

                scrollFlow = CreateScoreFlow();
                scrollFlow.ChildrenEnumerable = scores.Select((s, index) => CreateDrawableScore(s, index + 1));

                // schedule because we may not be loaded yet (LoadComponentAsync complains).
                showScoresDelegate?.Cancel();
                if (!IsLoaded)
                    showScoresDelegate = Schedule(showScores);
                else
                    showScores();

                void showScores() => LoadComponentAsync(scrollFlow, _ =>
                {
                    scrollContainer.Add(scrollFlow);

                    int i = 0;
                    foreach (var s in scrollFlow.Children)
                    {
                        using (s.BeginDelayedSequence(i++ * 50, true))
                            s.Show();
                    }

                    scrollContainer.ScrollTo(0f, false);
                });
            }
        }

        protected virtual FillFlowContainer<LeaderboardScore> CreateScoreFlow()
            => new FillFlowContainer<LeaderboardScore>
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Spacing = new Vector2(0f, 5f),
                Padding = new MarginPadding { Top = 10, Bottom = 5 },
            };

        private TScope scope;

        public TScope Scope
        {
            get { return scope; }
            set
            {
                if (value.Equals(scope))
                    return;

                scope = value;
                UpdateScores();
            }
        }

        private PlaceholderState placeholderState;

        /// <summary>
        /// Update the placeholder visibility.
        /// Setting this to anything other than PlaceholderState.Successful will cancel all existing retrieval requests and hide scores.
        /// </summary>
        protected PlaceholderState PlaceholderState
        {
            get { return placeholderState; }
            set
            {
                if (value != PlaceholderState.Successful)
                {
                    getScoresRequest?.Cancel();
                    getScoresRequest = null;
                    Scores = null;
                }

                if (value == placeholderState)
                    return;

                switch (placeholderState = value)
                {
                    case PlaceholderState.NetworkFailure:
                        replacePlaceholder(new RetrievalFailurePlaceholder
                        {
                            OnRetry = UpdateScores,
                        });
                        break;
                    case PlaceholderState.Unavailable:
                        replacePlaceholder(new MessagePlaceholder(@"Leaderboards are not available for this beatmap!"));
                        break;
                    case PlaceholderState.NoScores:
                        replacePlaceholder(new MessagePlaceholder(@"No records yet!"));
                        break;
                    case PlaceholderState.NotLoggedIn:
                        replacePlaceholder(new MessagePlaceholder(@"Please sign in to view online leaderboards!"));
                        break;
                    case PlaceholderState.NotSupporter:
                        replacePlaceholder(new MessagePlaceholder(@"Please invest in an osu!supporter tag to view this leaderboard!"));
                        break;
                    default:
                        replacePlaceholder(null);
                        break;
                }
            }
        }

        protected Leaderboard()
        {
            Children = new Drawable[]
            {
                scrollContainer = new OsuScrollContainer
                {
                    RelativeSizeAxes = Axes.Both,
                    ScrollbarVisible = false,
                },
                loading = new LoadingAnimation(),
                placeholderContainer = new Container
                {
                    RelativeSizeAxes = Axes.Both
                },
            };
        }

        private APIAccess api;

        private ScheduledDelegate pendingUpdateScores;

        [BackgroundDependencyLoader(true)]
        private void load(APIAccess api)
        {
            this.api = api;
            api?.Register(this);
        }

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);
            api?.Unregister(this);
        }

        public void RefreshScores() => UpdateScores();

        private APIRequest getScoresRequest;

        public void APIStateChanged(APIAccess api, APIState state)
        {
            if (state == APIState.Online)
                UpdateScores();
        }

        protected void UpdateScores()
        {
            // don't display any scores or placeholder until the first Scores_Set has been called.
            // this avoids scope changes flickering a "no scores" placeholder before initialisation of song select is finished.
            if (!scoresLoadedOnce) return;

            getScoresRequest?.Cancel();
            getScoresRequest = null;

            pendingUpdateScores?.Cancel();
            pendingUpdateScores = Schedule(() =>
            {
                if (api?.IsLoggedIn != true)
                {
                    PlaceholderState = PlaceholderState.NotLoggedIn;
                    return;
                }

                PlaceholderState = PlaceholderState.Retrieving;
                loading.Show();

                getScoresRequest = FetchScores(scores => Schedule(() =>
                {
                    Scores = scores;
                    PlaceholderState = Scores.Any() ? PlaceholderState.Successful : PlaceholderState.NoScores;
                }));

                if (getScoresRequest == null)
                    return;

                getScoresRequest.Failure += e => Schedule(() =>
                {
                    if (e is OperationCanceledException)
                        return;

                    PlaceholderState = PlaceholderState.NetworkFailure;
                });

                api.Queue(getScoresRequest);
            });
        }

        protected abstract APIRequest FetchScores(Action<IEnumerable<ScoreInfo>> scoresCallback);

        private Placeholder currentPlaceholder;

        private void replacePlaceholder(Placeholder placeholder)
        {
            if (placeholder != null && placeholder.Equals(currentPlaceholder))
                return;

            currentPlaceholder?.FadeOut(150, Easing.OutQuint).Expire();

            if (placeholder == null)
            {
                currentPlaceholder = null;
                return;
            }

            placeholderContainer.Child = placeholder;

            placeholder.ScaleTo(0.8f).Then().ScaleTo(1, fade_duration * 3, Easing.OutQuint);
            placeholder.FadeInFromZero(fade_duration, Easing.OutQuint);

            currentPlaceholder = placeholder;
        }

        protected virtual bool FadeBottom => true;
        protected virtual bool FadeTop => false;

        protected override void UpdateAfterChildren()
        {
            base.UpdateAfterChildren();

            var fadeBottom = scrollContainer.Current + scrollContainer.DrawHeight;
            var fadeTop = scrollContainer.Current + LeaderboardScore.HEIGHT;

            if (!scrollContainer.IsScrolledToEnd())
                fadeBottom -= LeaderboardScore.HEIGHT;

            if (scrollFlow == null)
                return;

            foreach (var c in scrollFlow.Children)
            {
                var topY = c.ToSpaceOfOtherDrawable(Vector2.Zero, scrollFlow).Y;
                var bottomY = topY + LeaderboardScore.HEIGHT;

                bool requireTopFade = FadeTop && topY <= fadeTop;
                bool requireBottomFade = FadeBottom && bottomY >= fadeBottom;

                if (!requireTopFade && !requireBottomFade)
                    c.Colour = Color4.White;
                else if (topY > fadeBottom + LeaderboardScore.HEIGHT || bottomY < fadeTop - LeaderboardScore.HEIGHT)
                    c.Colour = Color4.Transparent;
                else
                {
                    if (bottomY - fadeBottom > 0 && FadeBottom)
                        c.Colour = ColourInfo.GradientVertical(
                            Color4.White.Opacity(Math.Min(1 - (topY - fadeBottom) / LeaderboardScore.HEIGHT, 1)),
                            Color4.White.Opacity(Math.Min(1 - (bottomY - fadeBottom) / LeaderboardScore.HEIGHT, 1)));
                    else if (FadeTop)
                        c.Colour = ColourInfo.GradientVertical(
                            Color4.White.Opacity(Math.Min(1 - (fadeTop - topY) / LeaderboardScore.HEIGHT, 1)),
                            Color4.White.Opacity(Math.Min(1 - (fadeTop - bottomY) / LeaderboardScore.HEIGHT, 1)));
                }
            }
        }

        protected abstract LeaderboardScore CreateDrawableScore(ScoreInfo model, int index);
    }
}
