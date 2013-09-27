﻿namespace FlexSearch.Specs.UnitTests.Domain
{
    using FlexSearch.Api.Types;
    using FlexSearch.Specs.Helpers;
    using FlexSearch.Specs.Helpers.SubSpec;
    using FlexSearch.Validators;

    using FluentAssertions;

    public class AnalyzerSpec
    {
        #region Public Methods and Operators

        [Specification]
        public void AnalyzerApiTypeRelated()
        {
            AnalyzerProperties sut = null;
            "Given a new analyzer".Given(() => sut = new AnalyzerProperties());

            "'TokenizerName' should default to 'standardtokenizer'".Then(
                () => sut.Tokenizer.TokenizerName.Should().Be("standardtokenizer"));
            "'Filters' should not be null".Then(() => sut.Filters.Should().NotBeNull());
            "'Filters' cannot be set to null".Then(
                () =>
                {
                    sut.Filters = null;
                    sut.Filters.Should().NotBeNull();
                });
            "'Tokenizer' cannot be set to null".Then(
                () =>
                {
                    sut.Tokenizer = null;
                    sut.Tokenizer.Should().NotBeNull();
                });
        }

        [Thesis]
        [UnitAutoFixture]
        public void AnalyzerValidatorRelated(AnalyzerValidator sut)
        {
            "Given a new analyzer validator".Given(() => { });

            "Atleast one filter should be specified".Then(
                () =>
                {
                    var analyzerProperties = new AnalyzerProperties();
                    sut.Validate(analyzerProperties).IsValid.Should().BeFalse();
                });
        }

        #endregion
    }
}