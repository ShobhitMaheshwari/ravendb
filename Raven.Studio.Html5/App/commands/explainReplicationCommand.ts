import commandBase = require("commands/commandBase");
import database = require("models/database");
import collection = require("models/collection");
import getIndexTermsCommand = require("commands/getIndexTermsCommand");
import getCollectionsCountCommand = require("commands/getCollectionsCountCommand");

class explainReplicationCommand extends commandBase {

    constructor(private db: database, private documentId: string, private url: string, private databaseName: string) {
        super();
    }

    execute(): JQueryPromise<replicationExplanationForDocumentDto> {
        var args = {
            destinationUrl: this.url, 
            databaseName: this.databaseName
        };
        return this.query("/replication/explain/" + this.documentId, args, this.db);
    }

}

export = explainReplicationCommand;
